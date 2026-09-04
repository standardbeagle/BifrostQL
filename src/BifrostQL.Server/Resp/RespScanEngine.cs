using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// One page of a SCAN enumeration: the matched keys (already formatted as
    /// <c>&lt;table&gt;:&lt;pk…&gt;</c>) and the primary-key position to resume after. A null
    /// <see cref="NextAfterKey"/> means the enumeration is complete — the handler emits the terminal
    /// cursor <c>0</c>.
    /// </summary>
    internal readonly record struct RespScanPage(
        IReadOnlyList<string> Keys, IReadOnlyList<object?>? NextAfterKey);

    /// <summary>
    /// The keyset-pagination engine behind <c>SCAN</c>. A SCAN over <c>&lt;table&gt;:*</c> maps to the
    /// EXISTING <see cref="GqlObjectQuery"/> pagination surface — Sort (ORDER BY the primary key,
    /// ascending, in schema order), Filter (<c>WHERE pk &gt; last-cursor-position</c>) and Limit — executed
    /// through <see cref="IQueryIntentExecutor"/> under the caller's identity. Keyset (not OFFSET)
    /// pagination is used deliberately: it is stable under concurrent inserts and matches the Redis SCAN
    /// contract of eventual full iteration, and its opaque cursor encodes ONLY a primary-key position.
    ///
    /// <para><b>The cursor cannot widen visibility.</b> The cursor is decoded into a <c>pk &gt; …</c>
    /// predicate and nothing else; the tenant/policy/soft-delete transformer pipeline ANDs its own scope
    /// onto that predicate inside the executor (see <see cref="GqlObjectQuery.AddSqlParameterized"/> — the
    /// node's Filter is augmented with tenant/soft-delete scope before SQL is built), so even a forged
    /// cursor can at most skip ahead WITHIN the caller's own visible set — never step into rows outside it.
    /// The engine also projects ONLY the primary-key columns, so no other row data is materialized during
    /// enumeration.</para>
    /// </summary>
    internal static class RespScanEngine
    {
        /// <summary>
        /// Fetches one keyset page of primary keys for <paramref name="table"/> starting strictly after
        /// <paramref name="afterKey"/> (null → from the beginning). The query fetches
        /// <paramref name="pageSize"/> + 1 rows: if the extra row is present there are more pages and the
        /// last emitted row's PK becomes the next cursor position; otherwise the page is final.
        /// </summary>
        public static async Task<RespScanPage> ScanAsync(
            IQueryIntentExecutor executor,
            IDbTable table,
            string keyPrefix,
            IReadOnlyList<object?>? afterKey,
            int pageSize,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken)
        {
            var keyColumns = table.KeyColumns.ToList();
            var query = BuildScanQuery(table, keyColumns);
            if (afterKey is not null)
                query.Filter = BuildKeysetFilter(keyColumns, afterKey, table.DbName);
            // Peek one past the page so the terminal page can report cursor 0 without a trailing empty round-trip.
            query.Limit = pageSize + 1;

            var intent = new QueryIntent
            {
                Query = query,
                UserContext = new Dictionary<string, object?>(userContext),
                Endpoint = endpoint,
            };
            var result = await executor.ExecuteAsync(intent, cancellationToken);

            var hasMore = result.Rows.Count > pageSize;
            var emitted = hasMore ? result.Rows.Take(pageSize).ToList() : result.Rows;

            var keys = new List<string>(emitted.Count);
            foreach (var row in emitted)
                keys.Add(FormatKey(keyPrefix, keyColumns, row));

            var nextAfterKey = hasMore
                ? keyColumns.Select(c => emitted[^1].GetValueOrDefault(c.DbName)).ToList()
                : null;

            return new RespScanPage(keys, nextAfterKey);
        }

        /// <summary>Projects ONLY the primary-key columns, ordered ascending by the whole PK in schema order.</summary>
        private static GqlObjectQuery BuildScanQuery(IDbTable table, IReadOnlyList<ColumnDto> keyColumns)
        {
            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
            };
            foreach (var column in keyColumns)
            {
                query.ScalarColumns.Add(new GqlObjectColumn(column.DbName));
                query.Sort.Add($"{column.GraphQlName}_asc");
            }
            return query;
        }

        /// <summary>
        /// Builds the lexicographic "primary key strictly greater than <paramref name="afterValues"/>"
        /// predicate: for key columns c0…cN it is
        /// <c>(c0 &gt; v0) OR (c0 = v0 AND c1 &gt; v1) OR … OR (c0 = v0 AND … AND cN &gt; vN)</c>. For a
        /// single-column PK this collapses to <c>c0 &gt; v0</c>. Only the PK position is expressed — never
        /// any tenant/policy data (the pipeline ANDs that in independently).
        /// </summary>
        private static TableFilter BuildKeysetFilter(
            IReadOnlyList<ColumnDto> keyColumns, IReadOnlyList<object?> afterValues, string tableName)
        {
            static Dictionary<string, object?> Predicate(ColumnDto column, string op, object? value) =>
                new() { [column.GraphQlName] = new Dictionary<string, object?> { [op] = value } };

            if (keyColumns.Count == 1)
                return TableFilter.FromObject(
                    Predicate(keyColumns[0], FilterOperators.Gt, afterValues[0]), tableName);

            var orTerms = new List<object?>(keyColumns.Count);
            for (var i = 0; i < keyColumns.Count; i++)
            {
                if (i == 0)
                {
                    orTerms.Add(Predicate(keyColumns[0], FilterOperators.Gt, afterValues[0]));
                    continue;
                }

                var andParts = new List<object?>(i + 1);
                for (var j = 0; j < i; j++)
                    andParts.Add(Predicate(keyColumns[j], FilterOperators.Eq, afterValues[j]));
                andParts.Add(Predicate(keyColumns[i], FilterOperators.Gt, afterValues[i]));
                orTerms.Add(new Dictionary<string, object?> { ["and"] = andParts });
            }

            return TableFilter.FromObject(new Dictionary<string, object?> { ["or"] = orTerms }, tableName);
        }

        /// <summary>Formats a resolved row's PK as the Redis key <c>&lt;prefix&gt;:&lt;pk1&gt;[:&lt;pk2&gt;…]</c>,
        /// reusing the slice-2 key shape so the emitted key round-trips into GET/HGETALL for the same namespace.</summary>
        private static string FormatKey(
            string keyPrefix, IReadOnlyList<ColumnDto> keyColumns, IReadOnlyDictionary<string, object?> row)
        {
            var sb = new StringBuilder(keyPrefix);
            foreach (var column in keyColumns)
            {
                sb.Append(RespProtocol.KeySeparator);
                sb.Append(Convert.ToString(row.GetValueOrDefault(column.DbName), CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// The context a SCAN cursor is issued against and re-validated against on every continuation.
    /// Every field is folded into the cursor's MAC and NONE of them is transmitted: each is
    /// re-derived from the LIVE request. A cursor minted for one table, one MATCH pattern, or one
    /// caller therefore recomputes a different MAC and fails closed.
    ///
    /// <para>The page size is deliberately NOT bound: Redis treats COUNT as a hint a client may vary
    /// between calls of the same iteration, so binding it would reject legitimate clients.</para>
    /// </summary>
    internal readonly record struct RespScanBinding(
        string TableKey, string MatchPattern, string IdentityFingerprint);

    /// <summary>
    /// The opaque, integrity-protected SCAN cursor. Structurally the construction the LDAP paged
    /// results cookie, the OData <c>$skiptoken</c> and the gRPC page token already use.
    ///
    /// <para><b>The cursor carries POSITION ONLY</b> — the primary-key values to resume after, and
    /// the issue time. It never carries scope, tenant, policy or row content. Containment does not
    /// rest on the cursor: every page is fetched through <c>IQueryIntentExecutor</c>, which ANDs
    /// tenant, soft-delete and policy predicates onto the query unconditionally, so a cursor
    /// pointing anywhere still resolves to at most the caller's own visible rows. The MAC is the
    /// tamper and replay guard, not the authorization boundary — it stops a client paging one table
    /// and then swapping in another mid-sequence, or replaying another principal's position.</para>
    ///
    /// <para><b>Forgery, tampering, cross-context replay and expiry are ONE outcome</b>: the same
    /// refusal, so none of them is distinguishable from the others. The MAC compare runs
    /// unconditionally and in constant time BEFORE the payload is parsed, so a decode fault cannot
    /// short-circuit the integrity gate (protocol-adapter-security invariant 2). A cursor that does
    /// not validate is refused EXPLICITLY — never treated as "start from the top", which would turn
    /// a tampered cursor into a silent full re-scan.</para>
    /// </summary>
    internal static class RespScanCursor
    {
        /// <summary>
        /// Version tag folded into the MAC. Bumping it makes every previously issued cursor fail
        /// closed rather than being reinterpreted under a new payload layout.
        /// </summary>
        private const string Version = "respscan1";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        /// <summary>Mints the cursor that resumes strictly after <paramref name="segments"/>.</summary>
        public static string Issue(
            IReadOnlyList<string> segments, DateTimeOffset issuedAt, RespScanBinding binding, byte[] secret)
        {
            ArgumentNullException.ThrowIfNull(segments);
            ArgumentNullException.ThrowIfNull(secret);

            var payload = issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                          + "|" + JsonSerializer.Serialize(segments, JsonOptions);
            return Base64Url(Encoding.UTF8.GetBytes(payload)) + "." + Base64Url(ComputeMac(payload, binding, secret));
        }

        /// <summary>Mints a cursor from raw key-column values, rendered invariantly.</summary>
        public static string Issue(
            IReadOnlyList<object?> values, DateTimeOffset issuedAt, RespScanBinding binding, byte[] secret) =>
            Issue(
                values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty).ToList(),
                issuedAt, binding, secret);

        /// <summary>
        /// Validates <paramref name="cursor"/> against a binding re-derived from the LIVE request.
        /// The Redis start sentinel <c>0</c> validates with <paramref name="segments"/> = null (begin
        /// from the first row). Anything else that does not validate returns false — never a partial
        /// or defaulted position.
        /// </summary>
        public static bool TryValidate(
            string cursor,
            RespScanBinding binding,
            byte[] secret,
            DateTimeOffset now,
            TimeSpan ttl,
            out IReadOnlyList<string>? segments)
        {
            ArgumentNullException.ThrowIfNull(secret);
            segments = null;

            if (cursor == RespProtocol.ScanStartCursor)
                return true;
            if (string.IsNullOrEmpty(cursor))
                return false;

            byte[] payloadBytes;
            byte[] presentedMac;
            try
            {
                var dot = cursor.IndexOf('.');
                if (dot <= 0 || dot == cursor.Length - 1)
                    return false;
                payloadBytes = FromBase64Url(cursor[..dot]);
                presentedMac = FromBase64Url(cursor[(dot + 1)..]);
            }
            // Decoding untrusted wire text catches the full parse family, not just FormatException:
            // a truncated or over-length segment must become a clean refusal, never an unhandled
            // fault on the connection loop (invariant 5).
            catch (Exception ex) when (ex is FormatException or ArgumentException
                                          or DecoderFallbackException or IndexOutOfRangeException)
            {
                return false;
            }

            string payload;
            try
            {
                payload = Encoding.UTF8.GetString(payloadBytes);
            }
            catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException)
            {
                return false;
            }

            // Unconditional constant-time compare, computed BEFORE the payload is parsed. Gating it
            // behind a successful parse would let a malformed payload skip the integrity check and
            // would leak, through timing, which cursors parse (invariant 2).
            var expectedMac = ComputeMac(payload, binding, secret);
            var macOk = CryptographicOperations.FixedTimeEquals(presentedMac, expectedMac);

            var parsed = TryParsePayload(payload, out var candidate, out var issuedAtUnix);
            if (!macOk || !parsed)
                return false;

            // An authentic but stale cursor fails exactly like a forged one — the same outcome, with
            // no oracle separating "expired" from "never valid".
            var age = now - DateTimeOffset.FromUnixTimeSeconds(issuedAtUnix);
            if (age < TimeSpan.Zero || age > ttl)
                return false;

            segments = candidate;
            return true;
        }

        /// <summary>
        /// The stable identity fingerprint bound into a cursor so a different principal cannot replay
        /// it. Built from the scalar and string-sequence entries of the session's user context — the
        /// same values the pipeline scopes on — then hashed, so no identity plaintext reaches the
        /// wire. Opaque entries are excluded: an unstable fingerprint would reject a principal's own
        /// valid cursors.
        /// </summary>
        public static string FingerprintIdentity(IDictionary<string, object?>? userContext)
        {
            if (userContext is null)
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(" anonymous")));

            var parts = new List<string>();
            foreach (var entry in userContext)
            {
                var rendered = RenderClaim(entry.Value);
                if (rendered is not null)
                    parts.Add(entry.Key + "=" + rendered);
            }
            parts.Sort(StringComparer.Ordinal);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
        }

        private static bool TryParsePayload(string payload, out IReadOnlyList<string>? segments, out long issuedAtUnix)
        {
            segments = null;
            issuedAtUnix = 0;

            var pipe = payload.IndexOf('|');
            if (pipe <= 0)
                return false;
            // TryParse rather than Parse: a well-formed but out-of-range component throws
            // OverflowException from the throwing overloads, which here would escape the handler.
            if (!long.TryParse(payload[..pipe], NumberStyles.Integer, CultureInfo.InvariantCulture, out issuedAtUnix))
                return false;

            try
            {
                var decoded = JsonSerializer.Deserialize<List<string>>(payload[(pipe + 1)..], JsonOptions);
                if (decoded is null || decoded.Count == 0)
                    return false;
                segments = decoded;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static byte[] ComputeMac(string payload, RespScanBinding binding, byte[] secret)
        {
            // Length-prefixed fields make the canonical form injective: two distinct bindings cannot
            // render to the same bytes, so they cannot share a MAC by construction rather than luck.
            var canonical = new StringBuilder()
                .Append(Version).Append('\n')
                .Append(Prefixed(binding.TableKey)).Append('\n')
                .Append(Prefixed(binding.MatchPattern)).Append('\n')
                .Append(Prefixed(binding.IdentityFingerprint)).Append('\n')
                .Append(Prefixed(payload))
                .ToString();

            return HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical));
        }

        private static string Prefixed(string value) =>
            value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

        private static string? RenderClaim(object? value) => value switch
        {
            null => null,
            string s => s,
            bool or byte or short or int or long or Guid => Convert.ToString(value, CultureInfo.InvariantCulture),
            IEnumerable<string> sequence => "[" + string.Join(",", sequence) + "]",
            IEnumerable enumerable => "[" + string.Join(",",
                enumerable.Cast<object?>().Select(x => x?.ToString() ?? string.Empty)) + "]",
            _ => null,
        };

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] FromBase64Url(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = (padded.Length % 4) switch
            {
                2 => padded + "==",
                3 => padded + "=",
                0 => padded,
                _ => throw new FormatException("invalid base64url length."),
            };
            return Convert.FromBase64String(padded);
        }
    }
}
