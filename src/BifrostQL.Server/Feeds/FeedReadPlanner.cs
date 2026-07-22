using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Feeds
{
    /// <summary>
    /// Raised when caller-supplied feed request input (the <c>since</c> boundary or the <c>limit</c>)
    /// is malformed. A dedicated user-facing type so the slice-3 endpoint can map it to a clean 400
    /// without leaking internal detail — the parse family is collapsed to this here so an overflow or
    /// format fault never escapes as an unhandled exception (.claude/rules/protocol-adapter-security.md
    /// invariant 5).
    /// </summary>
    public sealed class FeedRequestException : Exception
    {
        public FeedRequestException(string message) : base(message) { }
    }

    /// <summary>
    /// Raised when a feed table's configured shape or a row's data cannot produce a well-formed feed
    /// item (missing column, null primary-key/timestamp, unsupported key type). Fail-closed: the feed
    /// stops rather than emit an item with an ambiguous or unstable identity.
    /// </summary>
    public sealed class FeedException : Exception
    {
        public FeedException(string message) : base(message) { }
    }

    /// <summary>
    /// A validated feed read request: an optional UTC <see cref="Since"/> lower bound and an optional
    /// requested <see cref="Limit"/>. Both are produced by <see cref="Parse"/>, which is the trust
    /// boundary for the untrusted wire values — it never throws a raw parse exception.
    /// </summary>
    public sealed record FeedRequest(DateTime? Since, int? Limit)
    {
        /// <summary>
        /// Parses the untrusted <paramref name="since"/> (ISO-8601 date/time) and <paramref name="limit"/>
        /// (integer) query values. <paramref name="since"/> is normalized to UTC regardless of the
        /// offset it carries, so the boundary the planner applies is always a UTC instant. Every
        /// malformed/overflowing value — including a 29-digit limit — collapses to a clean
        /// <see cref="FeedRequestException"/>, never an unhandled parse fault.
        /// </summary>
        public static FeedRequest Parse(string? since, string? limit)
        {
            DateTime? sinceUtc = null;
            if (!string.IsNullOrWhiteSpace(since))
            {
                try
                {
                    var parsed = DateTimeOffset.Parse(
                        since, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                    sinceUtc = parsed.UtcDateTime;
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
                {
                    throw new FeedRequestException("Invalid 'since' value; expected an ISO-8601 UTC date/time.");
                }
            }

            int? requestedLimit = null;
            if (!string.IsNullOrWhiteSpace(limit))
            {
                // TryParse returns false (never throws) on a malformed or overflowing value, so the
                // whole parse-exception family — a 29-digit limit included — is one clean rejection.
                if (!int.TryParse(limit.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                    throw new FeedRequestException("Invalid 'limit' value; expected an integer.");
                if (value < 0)
                    throw new FeedRequestException("'limit' must be non-negative.");
                requestedLimit = value;
            }

            return new FeedRequest(sinceUtc, requestedLimit);
        }
    }

    /// <summary>
    /// Plans and executes a table-backed syndication feed read. Feed tables and their configured
    /// fields are resolved only from the cached <see cref="IDbModel"/> (via <see cref="FeedConfig"/>);
    /// no route, column, sort, or filter name is ever interpolated into GraphQL or SQL text — the
    /// planner builds a programmatic <see cref="GqlObjectQuery"/> and executes it EXCLUSIVELY through
    /// <see cref="IQueryIntentExecutor"/>, so tenant isolation, soft-delete, policy row/column scope,
    /// field-crypto and every other query transformer apply unskippably. The only predicate the
    /// adapter contributes is its declared <c>since</c> lower bound, AND-composed with the pipeline's
    /// filters downstream (.claude/rules/protocol-adapter-security.md, read-seam invariant).
    /// </summary>
    public sealed class FeedReadPlanner
    {
        // A fixed namespace so item ids are RFC-4122 v5 (name-based) and stable across processes.
        private static readonly Guid FeedNamespace = new("6f2d5e1a-3b7c-4f9a-8c2e-1d0b9a7c6e50");

        private readonly IQueryIntentExecutor _reads;

        public FeedReadPlanner(IQueryIntentExecutor reads)
            => _reads = reads ?? throw new ArgumentNullException(nameof(reads));

        /// <summary>
        /// Builds the programmatic query for a feed read: projects only the timestamp, body, template,
        /// and full primary-key columns; orders timestamp DESC with every primary-key component
        /// ascending as a deterministic tiebreak (composite-safe, never a first-key shortcut); bounds
        /// the limit under the server maximum; and sets the sole <c>since</c> predicate. No other
        /// WHERE is built — the pipeline scopes rows from the caller's identity.
        /// </summary>
        public static GqlObjectQuery BuildQuery(IDbTable table, FeedConfig config, FeedRequest request, FeedOptions options)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(options);

            if (!config.IsEnabled)
                throw new FeedException($"Table '{table.GraphQlName}' is not a feed (no feed-timestamp configured).");

            var timestampColumn = ResolveColumn(table, config.TimestampColumn!, "feed timestamp");
            var keyColumns = table.KeyColumns.ToArray();
            if (keyColumns.Length == 0)
                throw new FeedException($"Feed table '{table.GraphQlName}' has no primary key; feed items cannot be identified.");

            var projected = new List<GqlObjectColumn>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Project(string dbName)
            {
                if (seen.Add(dbName))
                    projected.Add(new GqlObjectColumn(dbName));
            }

            Project(timestampColumn.DbName);
            if (config.BodyColumn is not null)
                Project(ResolveColumn(table, config.BodyColumn, "feed body").DbName);
            foreach (var dbName in TemplateColumns(table, config.TitleTemplate, isTitle: true))
                Project(dbName);
            foreach (var dbName in TemplateColumns(table, config.LinkTemplate, isTitle: false))
                Project(dbName);
            // Every primary-key component — the GUID and the tiebreak both need the whole key.
            foreach (var key in keyColumns)
                Project(key.DbName);

            var sort = new List<string> { $"{timestampColumn.GraphQlName}_desc" };
            var sortSeen = new HashSet<string>(StringComparer.Ordinal) { timestampColumn.GraphQlName };
            foreach (var key in keyColumns)
            {
                if (sortSeen.Add(key.GraphQlName))
                    sort.Add($"{key.GraphQlName}_asc");
            }

            var limit = Math.Min(request.Limit ?? options.DefaultItems, options.MaxItems);

            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
                Sort = sort,
                Limit = limit,
                Filter = BuildSinceFilter(table, timestampColumn, request.Since),
            };
            foreach (var column in projected)
                query.ScalarColumns.Add(column);
            return query;
        }

        /// <summary>
        /// Executes a feed read and materializes the format-neutral <see cref="FeedDocument"/>: the
        /// query is built by <see cref="BuildQuery"/> and run through <see cref="IQueryIntentExecutor"/>;
        /// each row becomes a <see cref="FeedItem"/> with a deterministic id, an expanded title/link,
        /// and its body. Channel/feed metadata comes from <paramref name="options"/>, never from rows.
        /// </summary>
        public async Task<FeedDocument> BuildAsync(
            IDbTable table, FeedRequest request, IDictionary<string, object?> userContext,
            FeedOptions options, string? endpoint = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(userContext);
            ArgumentNullException.ThrowIfNull(options);

            var config = FeedConfig.FromTable(table);
            var query = BuildQuery(table, config, request, options);

            var result = await _reads.ExecuteAsync(
                new QueryIntent { Query = query, UserContext = userContext, Endpoint = endpoint },
                cancellationToken);

            var timestampColumn = ResolveColumn(table, config.TimestampColumn!, "feed timestamp");
            var keyColumns = table.KeyColumns.ToArray();

            var items = new List<FeedItem>(result.Rows.Count);
            foreach (var row in result.Rows)
            {
                var timestamp = ReadTimestamp(row, timestampColumn);
                items.Add(new FeedItem
                {
                    Guid = ComputeItemGuid(table, keyColumns, row, timestamp),
                    Title = ExpandTemplate(config.TitleTemplate, isTitle: true, table, row) ?? string.Empty,
                    Body = config.BodyColumn is null ? null : ColumnValue(table, config.BodyColumn, row),
                    Link = ExpandTemplate(config.LinkTemplate, isTitle: false, table, row),
                    Timestamp = timestamp,
                });
            }

            return new FeedDocument
            {
                Title = options.Title,
                Link = options.Link,
                Description = options.Description,
                Author = options.Author,
                FeedId = options.Link,
                Updated = items.Count > 0 ? items.Max(item => item.Timestamp) : null,
                Items = items,
            };
        }

        // ---- template expansion (slice-1 validated grammar only) --------------------------------

        /// <summary>
        /// Expands a feed template using ONLY the slice-1-validated placeholder grammar
        /// (<see cref="FeedConfig.GetPlaceholders"/>) and schema-derived values — no custom parser.
        /// A title with no placeholders is the slice-1 bare-column shorthand (its value comes from a
        /// row); a link with no placeholders is literal text. Placeholder values are inserted raw; the
        /// writers XML-escape them.
        /// </summary>
        private static string? ExpandTemplate(string? template, bool isTitle, IDbTable table, IReadOnlyDictionary<string, object?> row)
        {
            if (template is null)
                return null;

            var placeholders = FeedConfig.GetPlaceholders(template).Distinct(StringComparer.Ordinal).ToArray();
            if (placeholders.Length == 0)
                return isTitle ? ColumnValue(table, template, row) : template;

            // Single-pass expansion: scan the template once, substituting each placeholder as it is
            // reached. A substituted row value is appended straight to the output and is never
            // re-scanned, so a row value that itself contains "{another-placeholder}" stays inert
            // literal text instead of being re-expanded (a sequential String.Replace loop would let a
            // value injected by an earlier placeholder be re-expanded by a later one).
            var valid = new HashSet<string>(placeholders, StringComparer.Ordinal);
            var expanded = new StringBuilder(template.Length);
            var cursor = 0;
            while (cursor < template.Length)
            {
                var open = template.IndexOf('{', cursor);
                if (open < 0)
                {
                    expanded.Append(template, cursor, template.Length - cursor);
                    break;
                }

                var close = template.IndexOf('}', open + 1);
                if (close < 0)
                {
                    expanded.Append(template, cursor, template.Length - cursor);
                    break;
                }

                expanded.Append(template, cursor, open - cursor);
                var name = template.Substring(open + 1, close - open - 1);
                if (valid.Contains(name))
                    expanded.Append(ColumnValue(table, name, row) ?? string.Empty);
                else
                    expanded.Append(template, open, close - open + 1); // not a grammar placeholder: literal

                cursor = close + 1;
            }

            return expanded.ToString();
        }

        private static IEnumerable<string> TemplateColumns(IDbTable table, string? template, bool isTitle)
        {
            if (template is null)
                yield break;

            var placeholders = FeedConfig.GetPlaceholders(template).Distinct(StringComparer.Ordinal).ToArray();
            if (placeholders.Length == 0)
            {
                // Bare title value = column shorthand; a literal link references no column.
                if (isTitle && TryGetSchemaColumn(table, template, out var column))
                    yield return column.DbName;
                yield break;
            }

            foreach (var name in placeholders)
                if (TryGetSchemaColumn(table, name, out var column))
                    yield return column.DbName;
        }

        private static string? ColumnValue(IDbTable table, string name, IReadOnlyDictionary<string, object?> row)
        {
            if (!TryGetSchemaColumn(table, name, out var column))
                throw new FeedException($"Feed template references '{name}', which is not a schema column.");
            return row.TryGetValue(column.DbName, out var value) && value is not null ? Stringify(value) : null;
        }

        // ---- deterministic item id --------------------------------------------------------------

        /// <summary>
        /// A deterministic RFC-4122 v5 id from the row's COMPLETE primary key plus its timestamp. A
        /// null key/timestamp value or an unsupported key type fails safely (throws) rather than emit
        /// an item with an ambiguous or unstable identity.
        /// </summary>
        private static string ComputeItemGuid(
            IDbTable table, IReadOnlyList<ColumnDto> keyColumns, IReadOnlyDictionary<string, object?> row, DateTime timestamp)
        {
            var canonical = new StringBuilder();
            canonical.Append(table.GraphQlName);
            foreach (var key in keyColumns)
            {
                canonical.Append('\u001F');
                if (!row.TryGetValue(key.DbName, out var value) || value is null)
                    throw new FeedException(
                        $"Feed row has a null primary-key value in column '{key.DbName}'; a stable item id requires every key component.");
                canonical.Append(CanonicalScalar(value, key.DbName));
            }
            canonical.Append('\u001F');
            canonical.Append(timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

            return Uuid5(FeedNamespace, canonical.ToString()).ToString("D");
        }

        private static string CanonicalScalar(object value, string column) => value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            Guid g => g.ToString("D"),
            DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new FeedException(
                $"Primary-key column '{column}' has unsupported type '{value.GetType().Name}' for a deterministic feed item id."),
        };

        private static Guid Uuid5(Guid namespaceId, string name)
        {
            var namespaceBytes = namespaceId.ToByteArray();
            SwapGuidByteOrder(namespaceBytes);
            var nameBytes = Encoding.UTF8.GetBytes(name);

            var input = new byte[namespaceBytes.Length + nameBytes.Length];
            Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
            Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);

            var hash = SHA1.HashData(input);
            var guidBytes = new byte[16];
            Array.Copy(hash, guidBytes, 16);
            guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50); // version 5
            guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC-4122 variant
            SwapGuidByteOrder(guidBytes);
            return new Guid(guidBytes);
        }

        // .NET Guid byte order is little-endian in the first three groups; RFC-4122 hashing uses the
        // big-endian ("network") order, so swap in and back out.
        private static void SwapGuidByteOrder(byte[] guid)
        {
            (guid[0], guid[3]) = (guid[3], guid[0]);
            (guid[1], guid[2]) = (guid[2], guid[1]);
            (guid[4], guid[5]) = (guid[5], guid[4]);
            (guid[6], guid[7]) = (guid[7], guid[6]);
        }

        // ---- shared column/value helpers --------------------------------------------------------

        private static DateTime ReadTimestamp(IReadOnlyDictionary<string, object?> row, ColumnDto timestampColumn)
        {
            if (!row.TryGetValue(timestampColumn.DbName, out var value) || value is null)
                throw new FeedException($"Feed row has a null timestamp in column '{timestampColumn.DbName}'.");

            return value switch
            {
                DateTime dt => (dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt).ToUniversalTime(),
                DateTimeOffset dto => dto.UtcDateTime,
                _ => throw new FeedException(
                    $"Feed timestamp column '{timestampColumn.DbName}' has unsupported type '{value.GetType().Name}'."),
            };
        }

        private static string Stringify(object value) => value switch
        {
            string s => s,
            DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        private static TableFilter? BuildSinceFilter(IDbTable table, ColumnDto timestampColumn, DateTime? since)
        {
            if (since is null)
                return null;

            // The GraphQL-shaped predicate the shared filter machinery binds as a SQL parameter — the
            // column is a schema-derived name, the value a bound parameter; nothing is spliced as text.
            var predicate = new Dictionary<string, object?>
            {
                { timestampColumn.GraphQlName, new Dictionary<string, object?> { { FilterOperators.Gte, since.Value } } },
            };
            return TableFilter.FromObject(predicate, table.DbName);
        }

        private static ColumnDto ResolveColumn(IDbTable table, string name, string role)
        {
            if (table.ColumnLookup.TryGetValue(name, out var column))
                return column;
            throw new FeedException($"Configured {role} column '{name}' does not exist on table '{table.GraphQlName}'.");
        }

        private static bool TryGetSchemaColumn(IDbTable table, string name, out ColumnDto column)
            => table.ColumnLookup.TryGetValue(name, out column!) || table.GraphQlLookup.TryGetValue(name, out column!);
    }
}
