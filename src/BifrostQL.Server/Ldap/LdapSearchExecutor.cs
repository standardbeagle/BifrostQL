using System.Security.Cryptography;
using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// The result of one search: the entries to write, the final result code, and the paged-results
    /// response control when the client asked for paging.
    /// </summary>
    internal sealed record LdapSearchOutcome(
        LdapResultCode ResultCode,
        string Diagnostic,
        IReadOnlyList<LdapSearchResultEntry> Entries,
        byte[]? ResponseControls)
    {
        public static LdapSearchOutcome Refused(LdapResultCode code, string diagnostic = "") =>
            new(code, diagnostic, Array.Empty<LdapSearchResultEntry>(), null);
    }

    /// <summary>
    /// Executes an LDAP SearchRequest as transformed query intents.
    ///
    /// <para><b>Reads go one way only.</b> Every row this class returns came from
    /// <see cref="IQueryIntentExecutor"/> under the bound session's identity. It builds no SQL and
    /// no scope predicate: the pipeline ANDs tenant, policy, and soft-delete filters onto every
    /// intent, so the filter compiled from the client's request can only ever NARROW what the
    /// caller was already permitted to read. There is no code path here that could widen it.</para>
    ///
    /// <para><b>Everything is bounded.</b> The effective result and time limits are the MINIMUM of
    /// the server's configured ceilings and whatever the client asked for, so a client can narrow
    /// them and never raise them. The membership fan-out has its own per-entry bound. Reaching a
    /// limit is reported (<c>sizeLimitExceeded</c>, <c>timeLimitExceeded</c>,
    /// <c>adminLimitExceeded</c>) rather than silently truncating, because a truncated result that
    /// looks complete is worse than an explicit partial one.</para>
    ///
    /// <para><b>One error funnel.</b> <see cref="ExecuteAsync"/> is the single place search errors
    /// map to wire codes, so no two op classes can drift apart in what they report for the same
    /// condition (protocol-adapter-security invariants 9 and 10). Internal exception text NEVER
    /// reaches the wire: a <see cref="Core.Model.BifrostExecutionError"/> can wrap raw driver or
    /// transformer detail (qualified table names, context-key names), so it is logged server-side and
    /// answered with a generic code (invariant 3).</para>
    ///
    /// <para><b>Anti-oracle.</b> A base object that names nothing, names something malformed, or
    /// names an entry whose row the identity cannot see all produce the SAME
    /// <c>noSuchObject</c> with an empty diagnostic. An authorization denial is reported as
    /// <c>insufficientAccessRights</c> WITHOUT naming what was denied.</para>
    /// </summary>
    internal sealed class LdapSearchExecutor
    {
        private readonly IQueryIntentExecutor _reads;
        private readonly LdapWireOptions _options;
        private readonly byte[] _cookieSecret;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ILogger _logger;

        public LdapSearchExecutor(
            IQueryIntentExecutor reads,
            LdapWireOptions options,
            byte[]? cookieSecret = null,
            Func<DateTimeOffset>? clock = null,
            ILogger? logger = null)
        {
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _cookieSecret = cookieSecret ?? DeriveCookieSecret(options, logger);
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// The single funnel. Every search — discovery or data, first page or continuation —
        /// returns through here, and every failure is mapped here, so the same condition cannot
        /// produce two different wire answers depending on which path reached it.
        /// </summary>
        public async Task<LdapSearchOutcome> ExecuteAsync(
            LdapSearchRequest request,
            IReadOnlyList<LdapControl> controls,
            LdapSessionState session,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(controls);
            ArgumentNullException.ThrowIfNull(session);

            // The effective deadline: the server's ceiling, narrowed by the client's timeLimit.
            // Linked to the connection token, so an Abandon or a dropped connection cancels the
            // work in flight rather than leaving it running against the database.
            var budget = EffectiveDuration(request.TimeLimit);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(budget);

            try
            {
                return await RunAsync(request, controls, session, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // The search's own deadline, not the connection's: a normal protocol outcome.
                return LdapSearchOutcome.Refused(LdapResultCode.TimeLimitExceeded);
            }
            catch (LdapMembershipLimitException ex)
            {
                // The bound is the server's, and the message names only a configured number.
                _logger.LogDebug(ex, "ldap search refused: membership fan-out bound exceeded.");
                return LdapSearchOutcome.Refused(LdapResultCode.AdminLimitExceeded);
            }
            catch (LdapProtocolException)
            {
                // A malformed control. Re-thrown so the SEARCH op class answers it as a
                // SearchResultDone carrying protocolError — the adapter's curated protocol text is
                // the one message class that is client-safe. It must not be swallowed into a
                // generic operationsError here: the client asked for a control this server does
                // implement and encoded it wrongly, which is a different thing to tell it than
                // "something went wrong on the server".
                throw;
            }
            catch (Exception ex)
            {
                // Everything else — a BifrostExecutionError wrapping driver/transformer text, a
                // model fault, a dialect fault. Its message may carry qualified table names or
                // context-key names, so the wire gets a generic code and NOTHING else; the detail
                // stays in the log (invariant 3).
                _logger.LogError(ex, "ldap search failed; answering with a generic error.");
                return LdapSearchOutcome.Refused(LdapResultCode.OperationsError);
            }
        }

        private async Task<LdapSearchOutcome> RunAsync(
            LdapSearchRequest request,
            IReadOnlyList<LdapControl> controls,
            LdapSessionState session,
            CancellationToken ct)
        {
            // RFC 4511 §4.1.11: an unsupported control the client marked CRITICAL makes the whole
            // operation unserviceable. Ignoring it would answer a question the client did not ask.
            if (LdapPagedResultsControl.FirstUnsupportedCritical(controls) is { } criticalOid)
            {
                _logger.LogDebug("ldap search refused: unsupported critical control {Oid}.", criticalOid);
                return LdapSearchOutcome.Refused(LdapResultCode.UnavailableCriticalExtension);
            }

            var paging = LdapPagedResultsControl.Decode(controls);

            var model = await _reads.GetModelAsync(_options.Endpoint);
            var index = LdapDirectoryIndex.Build(model);
            if (index is null)
            {
                // No table opts into the directory. This is a configuration state, not a client
                // secret — but it is answered with the same uniform code as any unaddressable base,
                // so probing cannot distinguish "no directory" from "not that entry".
                _logger.LogDebug("ldap search: no table is mapped into the directory.");
                return LdapSearchOutcome.Refused(LdapResultCode.NoSuchObject);
            }

            var resolution = index.Resolve(request.BaseObject, request.Scope);
            if (resolution.Kind == LdapScopeKind.NoSuchObject)
                return LdapSearchOutcome.Refused(LdapResultCode.NoSuchObject);

            var selection = LdapAttributeSelection.Parse(request.Attributes);

            // The anonymous session's ceiling, restated at the point of execution. The connection
            // loop already refuses a non-discovery base for an anonymous session; repeating it here
            // means the restriction holds for any future caller of this executor too, rather than
            // depending on one call site having remembered it.
            if (session.IsAnonymous
                && resolution.Kind is not (LdapScopeKind.RootDse or LdapScopeKind.Subschema))
            {
                return LdapSearchOutcome.Refused(
                    LdapResultCode.InsufficientAccessRights,
                    "anonymous access is limited to the RootDSE and the subschema");
            }

            switch (resolution.Kind)
            {
                case LdapScopeKind.RootDse:
                    return Discovery(LdapDirectoryEntries.RootDse(index.Model.RootDse), request, selection);

                case LdapScopeKind.Subschema:
                    return Discovery(
                        LdapDirectorySubschema.ForIdentity(index, session), request, selection);

                default:
                    return await SearchEntriesAsync(
                        index, resolution, request, selection, paging, session, ct);
            }
        }

        // RootDSE / subschema. These are entries like any other: the client's filter and attribute
        // selection apply, so a discovery read cannot dodge either.
        private static LdapSearchOutcome Discovery(
            LdapSearchResultEntry entry, LdapSearchRequest request, LdapAttributeSelection selection)
        {
            if (!LdapDiscoveryFilter.Matches(request.Filter, entry))
                return new LdapSearchOutcome(
                    LdapResultCode.Success, string.Empty, Array.Empty<LdapSearchResultEntry>(), null);

            var attributes = entry.Attributes
                .Where(a => selection.Includes(a.Type))
                .Select(a => new LdapPartialAttribute(
                    a.Type, request.TypesOnly ? Array.Empty<string>() : a.Values))
                .ToList();

            return new LdapSearchOutcome(
                LdapResultCode.Success, string.Empty,
                new[] { new LdapSearchResultEntry(entry.ObjectName, attributes) }, null);
        }

        private async Task<LdapSearchOutcome> SearchEntriesAsync(
            LdapDirectoryIndex index,
            LdapScopeResolution resolution,
            LdapSearchRequest request,
            LdapAttributeSelection selection,
            LdapPagedResultsRequest? paging,
            LdapSessionState session,
            CancellationToken ct)
        {
            // A session with no projected identity has no scope to narrow from. Failing closed here
            // is the difference between "the pipeline scopes an empty context to nothing" (which it
            // may or may not) and "this front door never runs an unscoped read" (which it does not).
            if (session.UserContext is not { Count: > 0 } userContext)
            {
                _logger.LogWarning("ldap search refused: the session carries no projected identity.");
                return LdapSearchOutcome.Refused(LdapResultCode.InsufficientAccessRights);
            }

            var pageSize = EffectivePageSize(paging, request.SizeLimit);
            var binding = new LdapPageBinding(
                LdapPageCookie.SearchShapeHash(
                    request.BaseObject, request.Scope, request.Filter, request.Attributes),
                pageSize,
                LdapPageCookie.FingerprintIdentity(userContext));

            var start = new LdapPagePosition(0, 0);
            if (paging is { Cookie.Length: > 0 })
            {
                // A cookie that does not validate is refused outright. Falling back to "start from
                // the beginning" would turn a tampered cookie into a silent full re-scan and would
                // hide the tampering from the client and the operator alike.
                if (!LdapPageCookie.TryDecode(
                        paging.Cookie, binding, _cookieSecret, _clock(), _options.PagedResultsCookieTtl,
                        out start))
                {
                    _logger.LogDebug("ldap search refused: the paged results cookie did not validate.");
                    return LdapSearchOutcome.Refused(LdapResultCode.UnavailableCriticalExtension);
                }
                if (start.TargetIndex >= resolution.Targets.Count)
                    return LdapSearchOutcome.Refused(LdapResultCode.UnavailableCriticalExtension);
            }

            var membership = new LdapMembershipResolver(_reads, index, _options);
            var entries = new List<LdapSearchResultEntry>();
            var truncated = false;
            var position = start;

            for (var targetIndex = start.TargetIndex; targetIndex < resolution.Targets.Count; targetIndex++)
            {
                var target = resolution.Targets[targetIndex];
                var offset = targetIndex == start.TargetIndex ? start.Offset : 0;

                var compiled = LdapFilterCompiler.Compile(request.Filter, target);
                var scoped = ScopeFilter(target, compiled, resolution);
                if (scoped is null)
                {
                    // The filter cannot match any entry of this family, decided from the mapping
                    // alone — no query is executed for it at all.
                    position = new LdapPagePosition(targetIndex + 1, 0);
                    continue;
                }

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var batch = await FetchBatchAsync(
                        target, selection, request, scoped, offset, userContext, ct);
                    if (batch.Count == 0)
                        break;

                    // The pushdown is an over-approximation, so the exact evaluation runs here and
                    // is what decides membership of the result set.
                    // Identity comparison, not value comparison: two distinct rows can hold equal
                    // values, and only the specific fetched instance is meant here.
                    var matchedSet = new HashSet<IReadOnlyDictionary<string, object?>>(
                        ReferenceEqualityComparer.Instance);
                    var matched = new List<IReadOnlyDictionary<string, object?>>();
                    foreach (var row in batch)
                    {
                        if (!LdapFilterEvaluator.Matches(request.Filter, target, row))
                            continue;
                        matchedSet.Add(row);
                        matched.Add(row);
                    }

                    var members = await ResolveMembersIfRequestedAsync(
                        membership, target, matched, selection, userContext, ct);
                    var memberOf = await ResolveMemberOfIfRequestedAsync(
                        membership, target, matched, selection, userContext, ct);

                    // The resume offset counts SOURCE rows consumed, not entries emitted, and not
                    // the size of the batch that was fetched. Advancing by the batch size would skip
                    // every row the page had no room for; advancing by the entry count would repeat
                    // every row the exact evaluation dropped. Both are silently wrong across a page
                    // boundary, which is exactly where a paging bug hides.
                    var consumed = 0;
                    foreach (var row in batch)
                    {
                        if (entries.Count >= pageSize)
                        {
                            truncated = true;
                            break;
                        }
                        consumed++;

                        if (!matchedSet.Contains(row))
                            continue;

                        var entry = LdapEntryProjector.Project(
                            target, row, selection, request.TypesOnly,
                            MemberDns(members, target, row), MemberDns(memberOf, target, row));
                        if (entry is not null)
                            entries.Add(entry);
                    }
                    offset += consumed;

                    if (truncated || entries.Count >= pageSize)
                    {
                        truncated = true;
                        position = new LdapPagePosition(targetIndex, offset);
                        break;
                    }

                    // A short batch means the ordered source is exhausted for this family.
                    if (batch.Count < _options.SearchBatchSize)
                        break;
                }

                if (truncated)
                    break;

                position = new LdapPagePosition(targetIndex + 1, 0);
            }

            return Complete(entries, truncated, paging, position, binding);
        }

        private LdapSearchOutcome Complete(
            List<LdapSearchResultEntry> entries,
            bool truncated,
            LdapPagedResultsRequest? paging,
            LdapPagePosition position,
            LdapPageBinding binding)
        {
            if (paging is null)
            {
                // No paging control: a client that asked for no paging and hit the ceiling is told
                // the answer is partial rather than being handed a truncated set that looks whole.
                return new LdapSearchOutcome(
                    truncated ? LdapResultCode.SizeLimitExceeded : LdapResultCode.Success,
                    string.Empty, entries, null);
            }

            // With paging, a full page is a normal success carrying a resume cookie. An EMPTY
            // cookie is the protocol's own "that was the last page", so a client always learns
            // where it stands without a second request.
            var cookie = truncated
                ? LdapPageCookie.Issue(position, _clock(), binding, _cookieSecret)
                : Array.Empty<byte>();

            return new LdapSearchOutcome(
                LdapResultCode.Success, string.Empty, entries,
                LdapPagedResultsControl.Encode(cookie));
        }

        // The client's filter, ANDed with the base-scope narrowing when the search names ONE entry.
        // Returns null when nothing can match. Note the direction: this only ever adds a predicate.
        private static IReadOnlyDictionary<string, object?>? ScopeFilter(
            LdapEntryTarget target, LdapCompiledFilter compiled, LdapScopeResolution resolution)
        {
            if (compiled.MatchesNothing)
                return null;

            if (resolution.Kind != LdapScopeKind.SingleEntry || resolution.NamingValue is null)
                return compiled.Pushdown ?? Empty();

            if (!target.Table.ColumnLookup.TryGetValue(target.NamingColumn, out var namingColumn))
                return null;

            // The RDN value is a PARAMETER, exactly like any filter value. It came from a client's
            // DN, so it is never interpolated into anything.
            var identity = new Dictionary<string, object?>
            {
                [namingColumn.GraphQlName] = new Dictionary<string, object?>
                {
                    [FilterOperators.Eq] = resolution.NamingValue,
                },
            };

            return compiled.Pushdown is { } pushdown
                ? new Dictionary<string, object?> { ["and"] = new List<object> { identity, pushdown } }
                : identity;

            static IReadOnlyDictionary<string, object?> Empty() =>
                new Dictionary<string, object?>();
        }

        private async Task<List<IReadOnlyDictionary<string, object?>>> FetchBatchAsync(
            LdapEntryTarget target,
            LdapAttributeSelection selection,
            LdapSearchRequest request,
            IReadOnlyDictionary<string, object?> predicate,
            int offset,
            IDictionary<string, object?> userContext,
            CancellationToken ct)
        {
            var query = new GqlObjectQuery
            {
                DbTable = target.Table,
                SchemaName = target.Table.TableSchema,
                TableName = target.Table.DbName,
                GraphQlName = target.Table.GraphQlName,
                Path = target.Table.GraphQlName,
                Limit = _options.SearchBatchSize,
                Offset = offset > 0 ? offset : null,
            };

            foreach (var column in LdapEntryProjector.RequiredColumns(target, selection, request.Filter))
                query.ScalarColumns.Add(new GqlObjectColumn(column.ColumnName));

            // A total order is required, not merely nice: without it the database may return rows in
            // a different order between pages, so paging over the result would repeat and skip
            // entries. The key columns give that order — all of them, so a composite key orders
            // fully rather than on its first column.
            foreach (var key in target.Table.KeyColumns)
                query.Sort.Add($"{key.GraphQlName}_asc");
            if (query.Sort.Count == 0)
                query.Sort.Add($"{NamingGraphQlName(target)}_asc");

            if (predicate.Count > 0)
                query.Filter = TableFilter.FromObject(
                    predicate.ToDictionary(kv => kv.Key, kv => kv.Value), target.Table.DbName);

            var result = await _reads.ExecuteAsync(
                new QueryIntent
                {
                    Query = query,
                    // A defensive COPY of the session's identity: this executor is shared across
                    // connections, so nothing downstream may hold or mutate the dictionary a
                    // session owns, and no request may observe another's context.
                    UserContext = new Dictionary<string, object?>(userContext),
                    Endpoint = _options.Endpoint,
                },
                ct);

            return result.Rows.ToList();
        }

        private static string NamingGraphQlName(LdapEntryTarget target) =>
            target.Table.ColumnLookup.TryGetValue(target.NamingColumn, out var column)
                ? column.GraphQlName
                : target.NamingColumn;

        private async Task<IReadOnlyDictionary<object, IReadOnlyList<string>>> ResolveMembersIfRequestedAsync(
            LdapMembershipResolver membership,
            LdapEntryTarget target,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
            LdapAttributeSelection selection,
            IDictionary<string, object?> userContext,
            CancellationToken ct)
        {
            if (!selection.Includes(LdapEntryProjector.MemberAttribute) || rows.Count == 0)
                return EmptyMembership;
            return await membership.ResolveMembersAsync(target, rows, userContext, ct);
        }

        private async Task<IReadOnlyDictionary<object, IReadOnlyList<string>>> ResolveMemberOfIfRequestedAsync(
            LdapMembershipResolver membership,
            LdapEntryTarget target,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
            LdapAttributeSelection selection,
            IDictionary<string, object?> userContext,
            CancellationToken ct)
        {
            if (!_options.MemberOfEnabled
                || !selection.Includes(LdapEntryProjector.MemberOfAttribute)
                || rows.Count == 0)
                return EmptyMembership;
            return await membership.ResolveMemberOfAsync(target, rows, userContext, ct);
        }

        private static readonly IReadOnlyDictionary<object, IReadOnlyList<string>> EmptyMembership =
            new Dictionary<object, IReadOnlyList<string>>();

        private static IReadOnlyList<string>? MemberDns(
            IReadOnlyDictionary<object, IReadOnlyList<string>> byKey,
            LdapEntryTarget target,
            IReadOnlyDictionary<string, object?> row)
        {
            if (byKey.Count == 0)
                return null;
            foreach (var key in target.Table.KeyColumns)
            {
                if (row.TryGetValue(key.ColumnName, out var value)
                    && value is not (null or DBNull)
                    && byKey.TryGetValue(value, out var dns))
                    return dns;
            }
            return null;
        }

        // The MINIMUM of the server's ceiling and the client's request. Zero means the client asked
        // for no limit of its own, which selects the server's -- never "unlimited".
        private TimeSpan EffectiveDuration(int clientSeconds)
        {
            if (clientSeconds <= 0)
                return _options.MaxSearchDuration;
            var requested = TimeSpan.FromSeconds(clientSeconds);
            return requested < _options.MaxSearchDuration ? requested : _options.MaxSearchDuration;
        }

        private int EffectivePageSize(LdapPagedResultsRequest? paging, int clientSizeLimit)
        {
            var size = _options.MaxSearchResults;
            if (clientSizeLimit > 0 && clientSizeLimit < size)
                size = clientSizeLimit;
            if (paging is { Size: > 0 })
            {
                var page = Math.Min(paging.Size, _options.MaxPageSize);
                if (page < size)
                    size = page;
            }
            return Math.Max(1, size);
        }

        private static byte[] DeriveCookieSecret(LdapWireOptions options, ILogger? logger)
        {
            if (!string.IsNullOrEmpty(options.PagedResultsCookieSecret))
                return Encoding.UTF8.GetBytes(options.PagedResultsCookieSecret);

            // A random per-process key is safe but not shared: outstanding cookies stop validating
            // on restart and across instances. That is a correctness surprise worth a warning, and
            // it is deliberately NOT resolved by falling back to a fixed default -- a predictable
            // key would make cookies forgeable.
            (logger ?? NullLogger.Instance).LogWarning(
                "ldap paged results cookies are keyed by a random per-process secret because "
                + "PagedResultsCookieSecret is unset; cookies will not survive a restart or work "
                + "across instances. Configure it explicitly for either.");
            return RandomNumberGenerator.GetBytes(32);
        }
    }
}
