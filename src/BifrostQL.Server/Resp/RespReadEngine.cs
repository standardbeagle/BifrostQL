using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
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
        /// Per-model table-name index for key parsing. Every RESP data command parses at least
        /// one key, and a linear scan over <see cref="IDbModel.Tables"/> per key is the single
        /// hottest lookup on the command path. Keyed by model identity so a reloaded model's
        /// fresh instance builds a fresh index and the old one falls away with the old model.
        /// A name that matches MORE THAN ONE table (same bare name in two schemas, or one
        /// table's DbName colliding with another's GraphQL name) is recorded as ambiguous and
        /// FAILS the lookup — silently binding the first model-order table would mis-target
        /// columns, policy metadata and the write target (same fail-fast contract as
        /// <c>DbModel.GetTableFromDbName</c>).
        /// </summary>
        private sealed record RespTableIndex(
            IReadOnlyDictionary<string, IDbTable> ByName,
            IReadOnlySet<string> Ambiguous);

        private static readonly ConditionalWeakTable<IDbModel, RespTableIndex> TableIndexCache = new();

        private static RespTableIndex GetTableIndex(IDbModel model) =>
            TableIndexCache.GetValue(model, static m =>
            {
                var byName = new Dictionary<string, IDbTable>(StringComparer.OrdinalIgnoreCase);
                var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var table in m.Tables)
                {
                    Register(table.DbName, table);
                    Register(table.GraphQlName, table);
                }
                return new RespTableIndex(byName, ambiguous);

                void Register(string name, IDbTable table)
                {
                    if (byName.TryGetValue(name, out var existing))
                    {
                        if (!ReferenceEquals(existing, table))
                            ambiguous.Add(name);
                    }
                    else
                    {
                        byName.Add(name, table);
                    }
                }
            });

        /// <summary>
        /// Columns in schema ordinal order, materialized once per table instance — RowToJson,
        /// VisibleFields and BuildRowQuery each re-sorted per row/command before this.
        /// </summary>
        private static readonly ConditionalWeakTable<IDbTable, ColumnDto[]> OrderedColumnsCache = new();

        private static ColumnDto[] OrderedColumns(IDbTable table) =>
            OrderedColumnsCache.GetValue(table, static t => t.Columns.OrderBy(c => c.OrdinalPosition).ToArray());

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
            var index = GetTableIndex(model);
            if (index.Ambiguous.Contains(tableName))
                return RespKeyParse.Failure(
                    $"{RespProtocol.ErrPrefix}table name '{tableName}' is ambiguous: more than one table matches; " +
                    "give the tables distinct names to address them over RESP");
            if (!index.ByName.TryGetValue(tableName, out var table))
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
        /// Keys are grouped by table; more than one requested key against one table is batched into
        /// ONE intent (DbTableBatchResolver-style: as few round-trips as possible) — a single-column
        /// PK via <c>_in</c>, a composite PK via an OR of per-key AND branches — and results are
        /// mapped back per key. A lone key resolves with an exact primary-key-equality intent. Every intent carries the caller's
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

                if (indices.Count > 1)
                {
                    if (keyColumns.Count == 1)
                        await ResolveSinglePkBatchAsync(executor, keys, indices, table, keyColumns[0], userContext, endpoint, results, cancellationToken);
                    else
                        await ResolveCompositePkBatchAsync(executor, keys, indices, table, keyColumns, userContext, endpoint, results, cancellationToken);
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
            foreach (var column in OrderedColumns(table))
                ordered[column.DbName] = row.GetValueOrDefault(column.DbName);
            return JsonSerializer.Serialize(ordered, JsonOptions);
        }

        /// <summary>
        /// The authoritative visible-column set for the field/value hash surface (HGETALL/HGET): the
        /// columns the transformer pipeline ACTUALLY RETURNED for this identity, in schema ordinal order.
        /// This intersects the model's columns with the returned row's keys — a column the pipeline
        /// masked/omitted (crypto masking, column policy) is simply absent from <paramref name="row"/>
        /// and therefore never surfaces here. Reflecting the returned columns (NOT the full
        /// <see cref="IDbTable.Columns"/> set) is what keeps HGETALL from exposing more than the pipeline
        /// authorized; a masked value present in the row is returned as-is (never unmasked).
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, object?>> VisibleFields(
            IReadOnlyDictionary<string, object?> row, IDbTable table)
        {
            var fields = new List<KeyValuePair<string, object?>>();
            foreach (var column in OrderedColumns(table))
                if (row.TryGetValue(column.DbName, out var value))
                    fields.Add(new KeyValuePair<string, object?>(column.DbName, value));
            return fields;
        }

        /// <summary>
        /// Resolves a single requested HGET field to its value IFF it is a column the pipeline returned
        /// for this identity. The field name is matched against the model column set (DB name or GraphQL
        /// name), then the RETURNED <paramref name="row"/> must actually carry that column. A column that
        /// exists in the model but was masked/omitted by the pipeline is reported as absent — exactly like
        /// an unknown field — so an existing-but-denied column never leaks its existence via a distinct
        /// outcome. Returns false (→ caller emits Null) in both the unknown-field and denied-column cases.
        /// </summary>
        public static bool TryResolveVisibleField(
            IReadOnlyDictionary<string, object?> row, IDbTable table, string fieldName, out object? value)
        {
            value = null;
            var column = table.Columns.FirstOrDefault(c =>
                string.Equals(c.DbName, fieldName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.GraphQlName, fieldName, StringComparison.OrdinalIgnoreCase));
            return column is not null && row.TryGetValue(column.DbName, out value);
        }

        /// <summary>
        /// Renders one column value as its scalar text for the field/value hash surface, consistent with
        /// <see cref="RowToJson"/>'s per-value serialization: a string passes through unquoted, every other
        /// scalar uses its JSON literal (numbers/booleans as-is, dates ISO-8601, byte arrays base64). A SQL
        /// NULL has no scalar text — the caller maps it to the protocol's Null — so this is only ever
        /// called for a non-null value.
        /// </summary>
        public static string FieldValueText(object? value)
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            return json.Length >= 2 && json[0] == '"'
                ? JsonSerializer.Deserialize<string>(json)!
                : json;
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

        /// <summary>
        /// Batches N composite-PK keys against one table into ONE intent: an OR whose branches each
        /// pin EVERY key column with <c>_eq</c> (sibling keys inside a branch AND together — see
        /// <c>TableFilter.StackFilters</c>), the composite counterpart of the single-PK <c>_in</c>
        /// batch. Identical requested keys collapse to one branch; rows map back per key through the
        /// same type-canonical token the single-PK batch uses, extended across all key columns. The
        /// one intent runs the full transformer pipeline under the caller's identity, so an
        /// out-of-scope row is simply absent — exactly as the per-key intents behaved.
        /// </summary>
        private static async Task ResolveCompositePkBatchAsync(
            IQueryIntentExecutor executor,
            IReadOnlyList<RespKey> keys,
            List<int> indices,
            IDbTable table,
            IReadOnlyList<ColumnDto> keyColumns,
            IDictionary<string, object?> userContext,
            string? endpoint,
            IReadOnlyDictionary<string, object?>?[] results,
            CancellationToken cancellationToken)
        {
            var branchByToken = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var i in indices)
            {
                var token = CompositeKeyToken(keyColumns, keys[i].KeyValues);
                if (token is null || branchByToken.ContainsKey(token))
                    continue;
                var branch = new Dictionary<string, object?>();
                for (var c = 0; c < keyColumns.Count; c++)
                    branch[keyColumns[c].GraphQlName] = new Dictionary<string, object?> { [FilterOperators.Eq] = keys[i].KeyValues[c] };
                branchByToken[token] = branch;
            }

            var query = BuildRowQuery(table);
            query.Filter = TableFilter.FromObject(
                new Dictionary<string, object?> { ["or"] = branchByToken.Values.ToList() },
                table.DbName);
            var result = await executor.ExecuteAsync(NewIntent(query, userContext, endpoint), cancellationToken);

            var byToken = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
            var rowKeyValues = new object?[keyColumns.Count];
            foreach (var row in result.Rows)
            {
                for (var c = 0; c < keyColumns.Count; c++)
                    rowKeyValues[c] = row.GetValueOrDefault(keyColumns[c].DbName);
                var token = CompositeKeyToken(keyColumns, rowKeyValues);
                if (token is not null)
                    byToken.TryAdd(token, row); // a PK is unique; first row wins defensively
            }

            foreach (var i in indices)
            {
                var token = CompositeKeyToken(keyColumns, keys[i].KeyValues);
                results[i] = token is not null && byToken.TryGetValue(token, out var row) ? row : null;
            }
        }

        /// <summary>
        /// One token for a full composite key: each column's <see cref="KeyToken"/> joined
        /// length-prefixed, so a separator character appearing INSIDE a string key value can never
        /// make two distinct composite keys collide. Null when any column's value is null (a PK
        /// column value is never legitimately null — such a row is unmatchable, same as the
        /// single-PK batch's null-token handling).
        /// </summary>
        private static string? CompositeKeyToken(IReadOnlyList<ColumnDto> keyColumns, IReadOnlyList<object?> values)
        {
            var builder = new StringBuilder();
            for (var c = 0; c < keyColumns.Count; c++)
            {
                var token = KeyToken(keyColumns[c], values[c]);
                if (token is null)
                    return null;
                builder.Append(token.Length).Append(':').Append(token);
            }
            return builder.ToString();
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
            foreach (var column in OrderedColumns(table))
                query.ScalarColumns.Add(new GqlObjectColumn(column.DbName));
            return query;
        }

        /// <summary>
        /// Coerces one key segment to the CLR type its column compares against — SQLite in particular
        /// will not equate the string '1' with the integer 1 through a parameter. Mirrors the MCP
        /// data-tool coercion (which BifrostQL.Server cannot reference); non-numeric columns keep the
        /// raw string. A numeric segment that does not parse is a clean, client-safe error.
        /// </summary>
        internal static bool TryCoerceKeySegment(ColumnDto column, string segment, out object? value, out string error)
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
