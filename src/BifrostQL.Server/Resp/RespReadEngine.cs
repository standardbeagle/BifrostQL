using System.Globalization;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// A parsed RESP key: the resolved table plus its primary-key values IN SCHEMA ORDER,
    /// already coerced to each key column's CLR type. A single-column PK is just the
    /// one-element case — nothing here special-cases arity 1 in a way that would break a
    /// composite key.
    /// </summary>
    internal sealed record RespKey(IDbTable Table, IReadOnlyList<object?> KeyValues);

    /// <summary>Outcome of parsing one RESP key: either a resolved <see cref="RespKey"/> or a clean, client-safe error.</summary>
    internal readonly record struct RespKeyParse(RespKey? Key, string? Error)
    {
        public bool Ok => Error is null;
        public static RespKeyParse Success(RespKey key) => new(key, null);
        public static RespKeyParse Failure(string error) => new(null, error);
    }

    /// <summary>
    /// The shared read engine behind the RESP key-space data commands (GET/MGET/EXISTS/TYPE).
    /// Redis keys are addressed as <c>&lt;table&gt;:&lt;pk1&gt;[:&lt;pk2&gt;…]</c>; this engine
    /// parses a key against the endpoint's <see cref="IDbModel"/>, maps the ordered segments to the
    /// table's primary-key columns IN SCHEMA ORDER (via <see cref="IDbTable.KeyColumns"/> and
    /// <see cref="TableFilter.FromPrimaryKey"/> — never <c>KeyColumns.First()</c>/<c>[0]</c>), and
    /// resolves each key to at most one row THROUGH <see cref="IQueryIntentExecutor"/> under the
    /// caller's identity. Every read therefore runs the security transformer pipeline (tenant
    /// isolation, soft-delete, policy row scope) unconditionally — the engine has no code path that
    /// reaches SQL directly. A row the identity cannot see comes back as no row, indistinguishable
    /// from a truly missing key, so existence is never leaked.
    ///
    /// <para><b>Row → JSON shape.</b> A found row is rendered as a JSON object mapping each column's
    /// database name to its value, in schema ordinal order; a SQL NULL is a JSON <c>null</c>. Values
    /// are serialized with System.Text.Json defaults (numbers/booleans as JSON scalars, dates as
    /// ISO-8601 strings, byte arrays as base64). The JSON text is returned as a RESP bulk string.</para>
    /// </summary>
    internal static class RespReadEngine
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        /// <summary>
        /// Parses <paramref name="rawKey"/> as <c>&lt;table&gt;:&lt;pk…&gt;</c> against the model.
        /// The table is validated against the model (unknown → clean error, never executed against an
        /// unvalidated name); the remaining segments must match the primary-key arity exactly
        /// (mismatch → clean error) and each is coerced to its key column's type (unparseable → clean
        /// error). Coerced values bind as query-intent parameters — a segment is never concatenated
        /// into SQL.
        /// </summary>
        public static RespKeyParse ParseKey(IDbModel model, string rawKey)
        {
            var segments = rawKey.Split(RespProtocol.KeySeparator);
            var tableName = segments[0];
            var table = model.Tables.FirstOrDefault(t =>
                string.Equals(t.DbName, tableName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.GraphQlName, tableName, StringComparison.OrdinalIgnoreCase));
            if (table is null)
                return RespKeyParse.Failure($"{RespProtocol.ErrPrefix}unknown table '{tableName}'");

            var keyColumns = table.KeyColumns.ToList();
            if (keyColumns.Count == 0)
                return RespKeyParse.Failure($"{RespProtocol.ErrPrefix}table '{table.DbName}' has no primary key");

            var pkSegments = segments.Skip(1).ToArray();
            if (pkSegments.Length != keyColumns.Count)
                return RespKeyParse.Failure(
                    $"{RespProtocol.ErrPrefix}key '{rawKey}' supplies {pkSegments.Length} value segment(s) " +
                    $"but table '{table.DbName}' has a {keyColumns.Count}-column primary key " +
                    $"({string.Join(", ", keyColumns.Select(c => c.ColumnName))})");

            var values = new object?[keyColumns.Count];
            for (var i = 0; i < keyColumns.Count; i++)
            {
                if (!TryCoerceKeySegment(keyColumns[i], pkSegments[i], out var value, out var error))
                    return RespKeyParse.Failure(error);
                values[i] = value;
            }
            return RespKeyParse.Success(new RespKey(table, values));
        }

        /// <summary>
        /// Resolves each key to at most one row, positionally aligned to <paramref name="keys"/>.
        /// Keys are grouped by table; a single-column-PK table with more than one requested key is
        /// batched into ONE <c>_in</c> intent (DbTableBatchResolver-style: as few round-trips as
        /// possible), and results are mapped back per key. Composite-PK keys (and a lone single-PK
        /// key) resolve with an exact primary-key-equality intent. Every intent carries the caller's
        /// <paramref name="userContext"/>, so tenant/soft-delete/policy filtering applies per key —
        /// an out-of-scope row simply yields <c>null</c>.
        /// </summary>
        public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>?>> ResolveRowsAsync(
            IQueryIntentExecutor executor,
            IReadOnlyList<RespKey> keys,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken)
        {
            var results = new IReadOnlyDictionary<string, object?>?[keys.Count];

            var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var i = 0; i < keys.Count; i++)
            {
                if (!groups.TryGetValue(keys[i].Table.DbName, out var indices))
                    groups[keys[i].Table.DbName] = indices = new List<int>();
                indices.Add(i);
            }

            foreach (var indices in groups.Values)
            {
                var table = keys[indices[0]].Table;
                var keyColumns = table.KeyColumns.ToList();

                if (keyColumns.Count == 1 && indices.Count > 1)
                {
                    await ResolveSinglePkBatchAsync(executor, keys, indices, table, keyColumns[0], userContext, endpoint, results, cancellationToken);
                    continue;
                }

                foreach (var i in indices)
                {
                    var query = BuildRowQuery(table);
                    query.Filter = TableFilter.FromPrimaryKey(keys[i].KeyValues, keyColumns, table.DbName);
                    query.Limit = 1;
                    var result = await executor.ExecuteAsync(NewIntent(query, userContext, endpoint), cancellationToken);
                    results[i] = result.Rows.Count > 0 ? result.Rows[0] : null;
                }
            }

            return results;
        }

        /// <summary>Renders a resolved row as the documented JSON object (column DB name → value, schema ordinal order).</summary>
        public static string RowToJson(IReadOnlyDictionary<string, object?> row, IDbTable table)
        {
            var ordered = new Dictionary<string, object?>();
            foreach (var column in table.Columns.OrderBy(c => c.OrdinalPosition))
                ordered[column.DbName] = row.GetValueOrDefault(column.DbName);
            return JsonSerializer.Serialize(ordered, JsonOptions);
        }

        private static async Task ResolveSinglePkBatchAsync(
            IQueryIntentExecutor executor,
            IReadOnlyList<RespKey> keys,
            List<int> indices,
            IDbTable table,
            ColumnDto pkColumn,
            IDictionary<string, object?> userContext,
            string? endpoint,
            IReadOnlyDictionary<string, object?>?[] results,
            CancellationToken cancellationToken)
        {
            var wanted = indices.Select(i => keys[i].KeyValues[0]).Distinct().ToList();
            var query = BuildRowQuery(table);
            query.Filter = TableFilter.FromObject(
                new Dictionary<string, object?>
                {
                    [pkColumn.GraphQlName] = new Dictionary<string, object?> { [FilterOperators.In] = wanted },
                },
                table.DbName);

            var result = await executor.ExecuteAsync(NewIntent(query, userContext, endpoint), cancellationToken);

            var byKey = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
            foreach (var row in result.Rows)
            {
                var token = KeyToken(pkColumn, row.GetValueOrDefault(pkColumn.DbName));
                if (token is not null)
                    byKey.TryAdd(token, row); // a PK is unique; first row wins defensively
            }

            foreach (var i in indices)
            {
                var token = KeyToken(pkColumn, keys[i].KeyValues[0]);
                results[i] = token is not null && byKey.TryGetValue(token, out var row) ? row : null;
            }
        }

        private static QueryIntent NewIntent(GqlObjectQuery query, IDictionary<string, object?> userContext, string? endpoint) =>
            new()
            {
                Query = query,
                UserContext = new Dictionary<string, object?>(userContext),
                Endpoint = endpoint,
            };

        private static GqlObjectQuery BuildRowQuery(IDbTable table)
        {
            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
            };
            foreach (var column in table.Columns.OrderBy(c => c.OrdinalPosition))
                query.ScalarColumns.Add(new GqlObjectColumn(column.DbName));
            return query;
        }

        /// <summary>
        /// Coerces one key segment to the CLR type its column compares against — SQLite in particular
        /// will not equate the string '1' with the integer 1 through a parameter. Mirrors the MCP
        /// data-tool coercion (which BifrostQL.Server cannot reference); non-numeric columns keep the
        /// raw string. A numeric segment that does not parse is a clean, client-safe error.
        /// </summary>
        private static bool TryCoerceKeySegment(ColumnDto column, string segment, out object? value, out string error)
        {
            error = string.Empty;
            switch (ClassifyKeyColumn(column))
            {
                case KeyColumnKind.Integer:
                    if (long.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    {
                        value = l;
                        return true;
                    }
                    break;
                case KeyColumnKind.Decimal:
                    if (decimal.TryParse(segment, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                    {
                        value = d;
                        return true;
                    }
                    break;
                default:
                    value = segment;
                    return true;
            }

            value = null;
            error = $"{RespProtocol.ErrPrefix}value '{segment}' is not valid for key column '{column.ColumnName}' ({column.DataType})";
            return false;
        }

        /// <summary>The CLR comparison family a PK column belongs to, derived once from its declared SQL type.</summary>
        private enum KeyColumnKind { Integer, Decimal, Other }

        /// <summary>
        /// Classifies a key column's declared SQL type into the family whose canonical form both
        /// <see cref="TryCoerceKeySegment"/> (request side) and <see cref="KeyToken"/> (DB-row side) use,
        /// so equal values always produce the same token regardless of which side materialized them.
        /// </summary>
        private static KeyColumnKind ClassifyKeyColumn(ColumnDto column)
        {
            var type = column.DataType.ToLowerInvariant();
            if (type.Contains("int"))
                return KeyColumnKind.Integer;
            if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("real")
                || type.Contains("float") || type.Contains("double") || type.Contains("money"))
                return KeyColumnKind.Decimal;
            return KeyColumnKind.Other;
        }

        /// <summary>
        /// Enough fractional '#' placeholders to render any <see cref="decimal"/> exactly while dropping
        /// trailing zeros — so <c>1</c>, <c>1.0</c>, and <c>1.00</c> collapse to the same canonical token
        /// but <c>1</c> and <c>1.5</c> never do.
        /// </summary>
        private const string DecimalCanonicalFormat = "0.############################";

        /// <summary>
        /// A culture-invariant, TYPE-CANONICAL token for matching a requested key value against a
        /// returned row's primary-key value. Because a numeric PK can arrive as one CLR form on the
        /// request side (the coerced segment) and a different scale/type on the DB-row side (e.g. the
        /// request <c>1.0</c> vs a stored <c>1</c>/<c>1.00</c>), both sides are normalized through the
        /// column's <see cref="KeyColumnKind"/>: integers via a common long form, decimals via a
        /// trailing-zero-stripped canonical form, everything else via its invariant string. Equal values
        /// therefore always collide, and distinct values never do.
        /// </summary>
        private static string? KeyToken(ColumnDto column, object? value)
        {
            if (value is null)
                return null;
            return ClassifyKeyColumn(column) switch
            {
                KeyColumnKind.Integer => Convert.ToInt64(value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture),
                KeyColumnKind.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
                    .ToString(DecimalCanonicalFormat, CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            };
        }
    }
}
