using System.Globalization;
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
    /// The opaque SCAN cursor codec. A cursor is a stable continuation token that carries ONLY the
    /// primary-key position to resume after — never any tenant/filter/identity data. The Redis start
    /// sentinel <c>0</c> means "begin", and the engine returns <c>0</c> when the last page is reached.
    ///
    /// <para>A non-start cursor is the Base64 of a JSON array of the key columns' invariant-string values.
    /// It is deliberately structured only enough to round-trip a PK position; because the tenant/policy
    /// filter is ANDed by the pipeline regardless of the cursor, a forged or hand-crafted cursor can never
    /// widen visibility — at worst it names a start position, and the pipeline still bounds the results to
    /// the caller's own rows.</para>
    /// </summary>
    internal static class RespScanCursor
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        /// <summary>Encodes a PK position (each key column's invariant-string value, in schema order) into an opaque token.</summary>
        public static string Encode(IReadOnlyList<string> segments)
        {
            var json = JsonSerializer.Serialize(segments, JsonOptions);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        /// <summary>Encodes a PK position from its raw column values (rendered invariantly).</summary>
        public static string Encode(IReadOnlyList<object?> values) =>
            Encode(values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty).ToList());

        /// <summary>
        /// Decodes a cursor into its PK-position segments. The start sentinel <c>0</c> yields
        /// <paramref name="segments"/> = null (begin from the first row). A structurally invalid cursor is a
        /// clean failure — it is never coerced into a filter, so a malformed cursor cannot execute anything.
        /// </summary>
        public static bool TryDecode(string cursor, out IReadOnlyList<string>? segments)
        {
            segments = null;
            if (cursor == RespProtocol.ScanStartCursor)
                return true;

            try
            {
                var bytes = Convert.FromBase64String(cursor);
                var decoded = JsonSerializer.Deserialize<List<string>>(Encoding.UTF8.GetString(bytes), JsonOptions);
                if (decoded is null || decoded.Count == 0)
                    return false;
                segments = decoded;
                return true;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
            {
                return false;
            }
        }
    }
}
