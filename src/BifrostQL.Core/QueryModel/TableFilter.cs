using BifrostQL.Core.Model;
using GraphQL;

namespace BifrostQL.Core.QueryModel
{
    public enum FilterType
    {
        And,
        Or,
        Relation,
        Join
    }
    public sealed class TableFilter
    {
        internal TableFilter() { }
        public string? TableName { get; init; }
        public string ColumnName { get; init; } = null!;
        public string RelationName { get; set; } = null!;
        public object? Value { get; set; }
        public FilterType FilterType { get; init; }
        public TableFilter? Next { get; set; }
        public List<TableFilter> And { get; init; } = new();
        public List<TableFilter> Or { get; init; } = new();

        public static TableFilter FromObject(object? value, string tableName)
        {
            var dictValue = value as Dictionary<string, object?> ?? throw new ExecutionError($"Error filtering {tableName}, null filter value");

            var filter = StackFilters(dictValue, tableName);
            if (filter.And.Count == 0 && filter.Or.Count == 0 && filter.Next == null)
                throw new ArgumentException("Invalid filter object", nameof(value));
            return filter;
        }

        private static TableFilter StackFilters(IDictionary<string, object?> filter, string? tableName)
        {
            if (!filter.Any()) throw new ExecutionError($"Filter on {tableName} has no properties");

            var kv = filter.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(kv.Key)) throw new ExecutionError($"Filter on {tableName} has empty property name");
            return kv switch
            {
                { Key: "and" } => new TableFilter
                {
                    And = ((IEnumerable<object>)kv.Value!).Select(v => StackFilters((IDictionary<string, object?>)v, tableName)).ToList(),
                    FilterType = FilterType.And,
                },
                { Key: "or" } => new TableFilter
                {
                    Or = ((IEnumerable<object>)kv.Value!).Select(v => StackFilters((IDictionary<string, object?>)v, tableName)).ToList(),
                    FilterType = FilterType.Or,
                },
                { Value: IDictionary<string, object?> val } => new TableFilter
                {
                    ColumnName = kv.Key!,
                    Next = StackFilters(val, null),
                    TableName = tableName,
                    FilterType = FilterType.Join,
                },
                { Value: null, Key: null } => throw new ExecutionError($"Filter on {tableName} has null key and value."),
                { Key: null } => throw new ExecutionError($"Filter on {tableName} has null key."),
                _ => new TableFilter
                {
                    RelationName = kv.Key,
                    Value = kv.Value,
                    FilterType = FilterType.Relation,
                },
            };
        }

        public static ParameterizedSql GetSingleFilterParameterized(
            ISqlDialect dialect,
            SqlParameterCollection parameters,
            string? table,
            string field,
            string op,
            object? value)
        {
            var columnRef = table == null
                ? dialect.EscapeIdentifier(field)
                : $"{dialect.EscapeIdentifier(table)}.{dialect.EscapeIdentifier(field)}";

            // Handle NULL comparisons (no parameters needed)
            if (op == "_eq" && value == null)
                return new ParameterizedSql($"{columnRef} IS NULL", Array.Empty<SqlParameterInfo>());
            if (op == "_neq" && value == null)
                return new ParameterizedSql($"{columnRef} IS NOT NULL", Array.Empty<SqlParameterInfo>());

            // Handle FieldRef (column-to-column comparison, no parameters)
            if (value is FieldRef fieldRef)
            {
                var refSql = fieldRef.TableName == null
                    ? dialect.EscapeIdentifier(fieldRef.ColumnName)
                    : $"{dialect.EscapeIdentifier(fieldRef.TableName)}.{dialect.EscapeIdentifier(fieldRef.ColumnName)}";
                return new ParameterizedSql($"{columnRef} {dialect.GetOperator(op)} {refSql}", Array.Empty<SqlParameterInfo>());
            }

            var sqlOp = dialect.GetOperator(op);

            // LIKE patterns
            if (op is "_contains" or "_ncontains")
            {
                var paramName = parameters.AddParameter(value);
                return new ParameterizedSql($"{columnRef} {sqlOp} {dialect.LikePattern(paramName, LikePatternType.Contains)}",
                    parameters.Parameters.TakeLast(1).ToList());
            }
            if (op is "_starts_with" or "_nstarts_with")
            {
                var paramName = parameters.AddParameter(value);
                return new ParameterizedSql($"{columnRef} {sqlOp} {dialect.LikePattern(paramName, LikePatternType.StartsWith)}",
                    parameters.Parameters.TakeLast(1).ToList());
            }
            if (op is "_ends_with" or "_nends_with")
            {
                var paramName = parameters.AddParameter(value);
                return new ParameterizedSql($"{columnRef} {sqlOp} {dialect.LikePattern(paramName, LikePatternType.EndsWith)}",
                    parameters.Parameters.TakeLast(1).ToList());
            }
            if (op is "_like" or "_nlike")
            {
                var paramName = parameters.AddParameter(value);
                return new ParameterizedSql($"{columnRef} {sqlOp} {paramName}",
                    parameters.Parameters.TakeLast(1).ToList());
            }

            // IN clause
            if (op is "_in" or "_nin")
            {
                var values = (value as IEnumerable<object?>) ?? Array.Empty<object?>();
                var paramNames = parameters.AddParameters(values);
                return new ParameterizedSql($"{columnRef} {sqlOp} ({paramNames})",
                    parameters.Parameters.TakeLast(values.Count()).ToList());
            }

            // BETWEEN clause
            if (op is "_between" or "_nbetween")
            {
                var values = ((value as IEnumerable<object?>) ?? Array.Empty<object?>()).ToArray();
                if (values.Length >= 2)
                {
                    var p1 = parameters.AddParameter(values[0]);
                    var p2 = parameters.AddParameter(values[1]);
                    return new ParameterizedSql($"{columnRef} {sqlOp} {p1} AND {p2}",
                        parameters.Parameters.TakeLast(2).ToList());
                }
            }

            // Simple comparison (default)
            var param = parameters.AddParameter(value);
            return new ParameterizedSql($"{columnRef} {sqlOp} {param}",
                parameters.Parameters.TakeLast(1).ToList());
        }

        public ParameterizedSql ToSqlParameterized(IDbModel model, ISqlDialect dialect, SqlParameterCollection parameters, string? alias = null)
        {
            if (Next == null)
            {
                if (And.Count > 0)
                {
                    var results = And.Select(f => f.ToSqlParameterized(model, dialect, parameters, alias)).ToArray();
                    var filters = results.Where(r => !string.IsNullOrWhiteSpace(r.Sql)).Select(r => r.Sql).ToArray();
                    var sql = filters.Length == 1 ? filters[0] : $"(({string.Join(") AND (", filters)}))";
                    return new ParameterizedSql(sql, results.SelectMany(r => r.Parameters).ToList());
                }
                if (Or.Count > 0)
                {
                    var results = Or.Select(f => f.ToSqlParameterized(model, dialect, parameters, alias)).ToArray();
                    var filters = results.Where(r => !string.IsNullOrWhiteSpace(r.Sql)).Select(r => r.Sql).ToArray();
                    var sql = filters.Length == 1 ? filters[0] : $"(({string.Join(") OR (", filters)}))";
                    return new ParameterizedSql(sql, results.SelectMany(r => r.Parameters).ToList());
                }
                throw new ExecutionError("Filter object missing all required fields.");
            }

            var table = model.GetTableFromDbName(TableName ?? throw new ExecutionError("TableFilter with undefined TableName"));
            if (Next.Next == null)
            {
                var lookup = table.GraphQlLookup;
                return GetSingleFilterParameterized(dialect, parameters, alias ?? TableName, lookup[ColumnName].DbName, Next.RelationName, Next.Value);
            }

            // For complex joins, use parameterized SQL throughout
            var link = table.SingleLinks[ColumnName];
            var (joinSql, joinParams) = BuildSqlParameterized(Next, link, dialect, parameters, includeValue: false);
            var (filterSql, filterParams) = GetFilterParameterized(Next, link, dialect, parameters, "j", includeValue: false);

            var fullJoin = $" INNER JOIN ({joinSql}) [j] ON [j].[joinid] = {dialect.EscapeIdentifier(alias ?? table.DbName)}.{dialect.EscapeIdentifier(table.SingleLinks[ColumnName].ChildId.ColumnName)}";
            var allParams = joinParams.Concat(filterParams).ToList();

            if (string.IsNullOrEmpty(filterSql))
                return new ParameterizedSql(fullJoin, allParams);
            return new ParameterizedSql(fullJoin + " WHERE " + filterSql, allParams);
        }

        private static (string sql, List<SqlParameterInfo> parameters) BuildSqlParameterized(
            TableFilter filter,
            TableLinkDto link,
            ISqlDialect dialect,
            SqlParameterCollection parameters,
            bool includeValue = false)
        {
            if (filter is { Next: { } } || (filter.Next == null && filter.And.Count > 0) || (filter.Next == null && filter.Or.Count > 0))
            {
                switch (filter.FilterType)
                {
                    case FilterType.Join
                        when link.ParentTable.SingleLinks.TryGetValue(filter.ColumnName, out var nextLink):
                        {
                            var (nextSql, nextParams) = BuildSqlParameterized(filter.Next!, nextLink, dialect, parameters);
                            var sql = $"SELECT DISTINCT {dialect.EscapeIdentifier(link.ParentId.ColumnName)} AS [joinid]{(includeValue ? ", [value]" : "")} FROM {dialect.EscapeIdentifier(link.ParentTable.DbName)} INNER JOIN ({nextSql}) [j] ON [j].[joinid] = {dialect.EscapeIdentifier(link.ParentTable.DbName)}.{dialect.EscapeIdentifier(nextLink.ChildId.ColumnName)}";
                            return (sql, nextParams);
                        }
                    case FilterType.Join:
                        if (includeValue)
                        {
                            return (
                                $"SELECT DISTINCT {dialect.EscapeIdentifier(link.ParentId.ColumnName)} AS [joinid], {dialect.EscapeIdentifier(filter.ColumnName)} AS [value] FROM {dialect.EscapeIdentifier(link.ParentTable.DbName)}",
                                new List<SqlParameterInfo>());
                        }
                        else
                        {
                            var filterResult = GetSingleFilterParameterized(dialect, parameters, link.ParentTable.DbName, filter.ColumnName, filter.Next!.RelationName, filter.Next.Value);
                            return (
                                $"SELECT DISTINCT {dialect.EscapeIdentifier(link.ParentId.ColumnName)} AS [joinid] FROM {dialect.EscapeIdentifier(link.ParentTable.DbName)} WHERE {filterResult.Sql}",
                                filterResult.Parameters.ToList());
                        }
                }
            }

            return ("", new List<SqlParameterInfo>());
        }

        private static (string sql, List<SqlParameterInfo> parameters) GetFilterParameterized(
            TableFilter filter,
            TableLinkDto link,
            ISqlDialect dialect,
            SqlParameterCollection parameters,
            string joinName,
            bool includeValue = false)
        {
            if (!includeValue) return ("", new List<SqlParameterInfo>());

            if (filter is { Next: { } } || (filter.Next == null && filter.And.Count > 0) || (filter.Next == null && filter.Or.Count > 0))
            {
                switch (filter.FilterType)
                {
                    case FilterType.Join
                        when link.ParentTable.SingleLinks.TryGetValue(filter.ColumnName, out var nextLink):
                        return GetFilterParameterized(filter.Next!, nextLink, dialect, parameters, joinName, includeValue);
                    case FilterType.Join:
                        var filterResult = GetSingleFilterParameterized(dialect, parameters, joinName, "value", filter.Next!.RelationName, filter.Next.Value);
                        return (filterResult.Sql, filterResult.Parameters.ToList());
                }
            }

            return ("", new List<SqlParameterInfo>());
        }

    }
}
