using System.Collections;
using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.QueryModel.VisualQuery
{
    /// <summary>
    /// Generated SQL plus its named parameters. <see cref="Parameters"/> keys are
    /// <c>@p0, @p1, ...</c> and feed straight into
    /// <see cref="RawSqlExecutor.ExecuteAsync"/>.
    /// </summary>
    public sealed record VisualQueryResult(string Sql, IReadOnlyDictionary<string, object?> Parameters);

    /// <summary>
    /// Turns a <see cref="VisualQuerySpec"/> into a parameterized SELECT using the
    /// active <see cref="ISqlDialect"/> for identifier quoting and pagination, and
    /// the <see cref="IDbModel"/> as the allow-list for every table and column.
    ///
    /// Security: no identifier or value is ever string-concatenated from raw spec
    /// input. Tables and columns are resolved against the model and rejected if
    /// absent (injection guard); all values flow through named parameters.
    ///
    /// Reference resolution: <see cref="VisualColumn.Table"/>,
    /// <see cref="VisualJoin.LeftTable"/>/<see cref="VisualJoin.RightTable"/>, and
    /// <see cref="VisualCriterion.Table"/> each name a table placed in
    /// <see cref="VisualQuerySpec.Tables"/> — matched by its <c>Alias</c> when set,
    /// otherwise by its qualified <c>Table</c> ("schema.name"). A table added twice
    /// must carry distinct aliases.
    /// </summary>
    public static class VisualQueryBuilder
    {
        /// <summary>Hard cap on returned rows; mirrors RawSqlExecutor's default.</summary>
        public const int MaxRows = 1000;

        public static VisualQueryResult Build(VisualQuerySpec spec, IDbModel model, ISqlDialect dialect)
        {
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(dialect);

            if (spec.Tables is null || spec.Tables.Count == 0)
                throw new BifrostExecutionError("Query must include at least one table.");

            var ctx = new BuildContext(spec, model, dialect);

            var selectClause = BuildSelect(ctx);
            var fromClause = BuildFromAndJoins(ctx);
            var whereClause = BuildWhere(ctx);
            var pagination = BuildPagination(ctx);

            var sql = new StringBuilder();
            sql.Append("SELECT ").Append(selectClause);
            sql.Append(' ').Append(fromClause);
            if (whereClause.Length > 0)
                sql.Append(" WHERE ").Append(whereClause);
            if (pagination.Length > 0)
                sql.Append(' ').Append(pagination);

            return new VisualQueryResult(sql.ToString(), ctx.Parameters);
        }

        // ---- SELECT --------------------------------------------------------

        private static string BuildSelect(BuildContext ctx)
        {
            var shown = ctx.Spec.Columns?.Where(c => c.Show).ToList() ?? [];
            if (shown.Count == 0)
                throw new BifrostExecutionError("Query must show at least one column.");

            var parts = new List<string>(shown.Count);
            foreach (var col in shown)
            {
                var reference = ctx.ColumnRef(col.Table, col.Column);
                if (!string.IsNullOrWhiteSpace(col.Alias))
                    reference += " AS " + ctx.Dialect.EscapeIdentifier(col.Alias!);
                parts.Add(reference);
            }
            return string.Join(", ", parts);
        }

        // ---- FROM + JOINs --------------------------------------------------

        private static string BuildFromAndJoins(BuildContext ctx)
        {
            var sb = new StringBuilder();

            // Base table is the first one placed on the surface.
            var baseEntry = ctx.Entries[0];
            sb.Append("FROM ").Append(ctx.TableSource(baseEntry));

            var joined = new HashSet<TableEntry> { baseEntry };
            var joins = ctx.Spec.Joins ?? [];

            // Greedily attach each remaining table via a join whose other side is
            // already joined. Spec order of Tables drives which table we try next,
            // so an explicit, valid ordering is honored; a disconnected table errors.
            for (var i = 1; i < ctx.Entries.Count; i++)
            {
                var target = ctx.Entries[i];

                var join = joins.FirstOrDefault(j =>
                {
                    var left = ctx.Lookup(j.LeftTable);
                    var right = ctx.Lookup(j.RightTable);
                    return (left == target && joined.Contains(right))
                        || (right == target && joined.Contains(left));
                });

                if (join is null)
                    throw new BifrostExecutionError(
                        $"Table '{DescribeEntry(target)}' is not connected to the query by any join.");

                sb.Append(' ').Append(BuildJoin(ctx, join, target));
                joined.Add(target);
            }

            return sb.ToString();
        }

        private static string BuildJoin(BuildContext ctx, VisualJoin join, TableEntry newTable)
        {
            var keyword = join.Type?.ToLowerInvariant() switch
            {
                VisualJoinType.Inner => "INNER JOIN",
                VisualJoinType.Left => "LEFT JOIN",
                _ => throw new BifrostExecutionError($"Unknown join type '{join.Type}'."),
            };

            var left = ctx.Lookup(join.LeftTable);
            var right = ctx.Lookup(join.RightTable);

            var leftCols = join.LeftColumns ?? [];
            var rightCols = join.RightColumns ?? [];
            if (leftCols.Count == 0 || leftCols.Count != rightCols.Count)
                throw new BifrostExecutionError(
                    "Join must have a matching, non-empty pair of column lists (composite keys join column-by-column).");

            var conditions = new List<string>(leftCols.Count);
            for (var i = 0; i < leftCols.Count; i++)
            {
                var l = ctx.ColumnRefForEntry(left, leftCols[i]);
                var r = ctx.ColumnRefForEntry(right, rightCols[i]);
                conditions.Add($"{l} = {r}");
            }

            return $"{keyword} {ctx.TableSource(newTable)} ON {string.Join(" AND ", conditions)}";
        }

        // ---- WHERE ---------------------------------------------------------

        private static string BuildWhere(BuildContext ctx)
        {
            return ctx.Spec.Filter is null ? string.Empty : BuildFilter(ctx, ctx.Spec.Filter);
        }

        private static string BuildFilter(BuildContext ctx, VisualFilter node)
        {
            switch (node.Op?.ToLowerInvariant())
            {
                case VisualFilterOp.Leaf:
                    if (node.Criterion is null)
                        throw new BifrostExecutionError("Leaf filter node must carry a criterion.");
                    return BuildCriterion(ctx, node.Criterion);

                case VisualFilterOp.And:
                case VisualFilterOp.Or:
                    var children = node.Children ?? [];
                    var rendered = children
                        .Select(c => BuildFilter(ctx, c))
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                    if (rendered.Count == 0)
                        return string.Empty;
                    var sep = node.Op.ToLowerInvariant() == VisualFilterOp.And ? " AND " : " OR ";
                    return "(" + string.Join(sep, rendered) + ")";

                default:
                    throw new BifrostExecutionError($"Unknown filter op '{node.Op}'.");
            }
        }

        private static string BuildCriterion(BuildContext ctx, VisualCriterion crit)
        {
            var colRef = ctx.ColumnRef(crit.Table, crit.Column);
            var op = crit.Operator;

            switch (op)
            {
                case FilterOperators.Null:
                    // value == true  -> IS NULL ; otherwise IS NOT NULL
                    var wantNull = crit.Value is bool b ? b : true;
                    return wantNull ? $"{colRef} IS NULL" : $"{colRef} IS NOT NULL";

                case FilterOperators.In:
                {
                    var values = AsEnumerable(crit.Value, op);
                    var names = values.Select(ctx.AddParameter).ToList();
                    if (names.Count == 0)
                        throw new BifrostExecutionError("_in requires at least one value.");
                    return $"{colRef} IN ({string.Join(", ", names)})";
                }

                case FilterOperators.Between:
                {
                    var values = AsEnumerable(crit.Value, op);
                    if (values.Count != 2)
                        throw new BifrostExecutionError("_between requires exactly two values.");
                    var lo = ctx.AddParameter(values[0]);
                    var hi = ctx.AddParameter(values[1]);
                    return $"{colRef} BETWEEN {lo} AND {hi}";
                }

                case FilterOperators.Contains:
                {
                    var name = ctx.AddParameter(crit.Value);
                    return $"{colRef} LIKE {ctx.Dialect.LikePattern(name, LikePatternType.Contains)}";
                }

                case FilterOperators.Eq:
                case FilterOperators.Neq:
                case FilterOperators.Lt:
                case FilterOperators.Lte:
                case FilterOperators.Gt:
                case FilterOperators.Gte:
                {
                    var name = ctx.AddParameter(crit.Value);
                    return $"{colRef} {ctx.Dialect.GetOperator(op)} {name}";
                }

                default:
                    throw new BifrostExecutionError($"Unsupported filter operator '{op}'.");
            }
        }

        private static IReadOnlyList<object?> AsEnumerable(object? value, string op)
        {
            if (value is string)
                throw new BifrostExecutionError($"{op} requires an array of values, not a single string.");
            if (value is IEnumerable e)
                return e.Cast<object?>().ToList();
            throw new BifrostExecutionError($"{op} requires an array of values.");
        }

        // ---- ORDER BY + pagination ----------------------------------------

        private static string BuildPagination(BuildContext ctx)
        {
            var sorted = (ctx.Spec.Columns ?? [])
                .Where(c => !string.Equals(c.Sort, VisualSort.None, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(c.Sort))
                .Select((c, idx) => (Column: c, Index: idx))
                .OrderBy(x => x.Column.SortOrder ?? int.MaxValue)
                .ThenBy(x => x.Index)
                .ToList();

            var orderExprs = new List<string>(sorted.Count);
            foreach (var (c, _) in sorted)
            {
                var dir = string.Equals(c.Sort, VisualSort.Desc, StringComparison.OrdinalIgnoreCase) ? " DESC" : " ASC";
                orderExprs.Add(ctx.ColumnRef(c.Table, c.Column) + dir);
            }

            var limit = ctx.Spec.RowLimit is int r
                ? (r <= 0 ? MaxRows : Math.Min(r, MaxRows))
                : MaxRows;

            // The dialect owns ORDER BY + OFFSET/FETCH vs LIMIT/OFFSET differences,
            // and (for SQL Server) supplies a default ORDER BY when none is given.
            return ctx.Dialect.Pagination(orderExprs.Count > 0 ? orderExprs : null, offset: null, limit: limit);
        }

        private static string DescribeEntry(TableEntry e) =>
            string.IsNullOrWhiteSpace(e.Spec.Alias) ? e.Spec.Table : $"{e.Spec.Table} ({e.Spec.Alias})";

        // ---- internal build state -----------------------------------------

        private sealed record TableEntry(VisualTable Spec, IDbTable Table, string SqlAlias);

        private sealed class BuildContext
        {
            public VisualQuerySpec Spec { get; }
            public ISqlDialect Dialect { get; }
            public List<TableEntry> Entries { get; } = [];
            public Dictionary<string, object?> Parameters { get; } = new(StringComparer.Ordinal);

            private readonly Dictionary<string, TableEntry> _byReference = new(StringComparer.OrdinalIgnoreCase);
            private int _paramCounter;

            public BuildContext(VisualQuerySpec spec, IDbModel model, ISqlDialect dialect)
            {
                Spec = spec;
                Dialect = dialect;

                for (var i = 0; i < spec.Tables.Count; i++)
                {
                    var vt = spec.Tables[i];
                    var table = ResolveTable(model, vt.Table);
                    var alias = string.IsNullOrWhiteSpace(vt.Alias) ? $"t{i}" : vt.Alias!;
                    var entry = new TableEntry(vt, table, alias);
                    Entries.Add(entry);

                    // Reference keys: the qualified table string, and the alias if set.
                    // Collisions mean the same table was placed twice without distinct aliases.
                    if (!string.IsNullOrWhiteSpace(vt.Alias))
                    {
                        if (!_byReference.TryAdd(vt.Alias!, entry))
                            throw new BifrostExecutionError($"Duplicate table alias '{vt.Alias}'.");
                    }
                    else if (!_byReference.TryAdd(vt.Table, entry))
                    {
                        throw new BifrostExecutionError(
                            $"Table '{vt.Table}' is placed more than once; give each instance a distinct alias.");
                    }
                }
            }

            public TableEntry Lookup(string reference)
            {
                if (reference is not null && _byReference.TryGetValue(reference, out var entry))
                    return entry;
                throw new BifrostExecutionError($"Unknown table reference '{reference}'. Add it to the query first.");
            }

            public string ColumnRef(string tableReference, string column) =>
                ColumnRefForEntry(Lookup(tableReference), column);

            public string ColumnRefForEntry(TableEntry entry, string column)
            {
                var col = ResolveColumn(entry.Table, column);
                return Dialect.EscapeIdentifier(entry.SqlAlias) + "." + Dialect.EscapeIdentifier(col.DbName);
            }

            public string TableSource(TableEntry entry) =>
                Dialect.TableReference(entry.Table.TableSchema, entry.Table.DbName)
                + " AS " + Dialect.EscapeIdentifier(entry.SqlAlias);

            public string AddParameter(object? value)
            {
                var name = Dialect.ParameterPrefix + "p" + _paramCounter++;
                Parameters[name] = value;
                return name;
            }

            private static IDbTable ResolveTable(IDbModel model, string qualified)
            {
                if (string.IsNullOrWhiteSpace(qualified))
                    throw new BifrostExecutionError("Table name must not be empty.");

                string? schema = null;
                var name = qualified;
                var dot = qualified.IndexOf('.');
                if (dot >= 0)
                {
                    schema = qualified[..dot];
                    name = qualified[(dot + 1)..];
                }

                var matches = model.Tables.Where(t =>
                    string.Equals(t.DbName, name, StringComparison.OrdinalIgnoreCase)
                    && (schema is null || string.Equals(t.TableSchema, schema, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (matches.Count == 0)
                    throw new BifrostExecutionError($"Table '{qualified}' was not found in the database model.");
                if (matches.Count > 1)
                    throw new BifrostExecutionError($"Table '{qualified}' is ambiguous; qualify it with a schema.");

                return matches[0];
            }

            private static ColumnDto ResolveColumn(IDbTable table, string column)
            {
                if (string.IsNullOrWhiteSpace(column))
                    throw new BifrostExecutionError("Column name must not be empty.");

                if (table.ColumnLookup.TryGetValue(column, out var c))
                    return c;

                c = table.Columns.FirstOrDefault(x =>
                    string.Equals(x.DbName, column, StringComparison.OrdinalIgnoreCase));
                if (c is not null)
                    return c;

                throw new BifrostExecutionError($"Column '{column}' was not found on table '{table.DbName}'.");
            }
        }
    }
}
