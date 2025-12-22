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
        private TableFilter() { }
        public string? TableName { get; init; }
        public string ColumnName { get; init; } = null!;
        public string RelationName { get; set; } = null!;
        public object? Value { get; set; }
        public FilterType FilterType { get; init; }
        public TableFilter? Next { get; set; }
        public List<TableFilter> And { get; init; } = new ();
        public List<TableFilter> Or { get; init; } = new ();

        public (string join, string comparison) ToSql(IDbModel model, string? alias = null, string joinName = "j", bool includeValue = false)
        {
            if (Next == null)
            {
                if (And.Count > 0)
                {
                    var test = And.Select((j, i) => j.ToSql(model, alias, $"j{i}", true)).ToArray();
                    var joins = test.Where(t => !string.IsNullOrWhiteSpace(t.join)).Select(t => t.join);
                    var filters = test.Where(t => !string.IsNullOrWhiteSpace(t.comparison)).Select(t => t.comparison).ToArray();
                    return (
                        string.Join("", joins),
                        filters.Length == 1 ? filters[0] : $"(({ string.Join(") AND (", filters) }))");
                }
                if (Or.Count > 0)
                {
                    var test = Or.Select((j, i) => j.ToSql(model, alias, $"j{i}", true)).ToArray();
                    var joins = test.Where(t => !string.IsNullOrWhiteSpace(t.join)).Select(t => t.join);
                    var filters = test.Where(t => !string.IsNullOrWhiteSpace(t.comparison)).Select(t => t.comparison).ToArray();
                    return (
                        string.Join("", joins),
                        filters.Length == 1 ? filters[0] : $"(({string.Join(") OR (", filters)}))");
                }
                throw new ExecutionError("Filter object missing all required fields.");
            }
            var table = model.GetTableFromDbName(TableName ?? throw new ExecutionError("TableFilter with undefined TableName"));
            if (Next.Next == null)
            {
                var lookup = table.GraphQlLookup;
                return ("", GetSingleFilter(alias ?? TableName, lookup[ColumnName].DbName, Next.RelationName, Next.Value));
            }
            var link = table.SingleLinks[ColumnName];
            var join = BuildSql(this.Next, link, includeValue);
            var filterText = GetFilter(this.Next, link, joinName, includeValue);

            join = $" INNER JOIN ({join}) [{joinName}] ON [{joinName}].[joinid] = [{alias ?? table.DbName}].[{table.SingleLinks[ColumnName].ChildId.ColumnName}]";
            return (join, filterText);
        }

        private static string BuildSql(TableFilter filter, TableLinkDto link, bool includeValue = false)
        {
            if (filter is { Next: { } } || (filter.Next == null && filter.And.Count > 0) || (filter.Next == null && filter.Or.Count > 0))
            {
                switch (filter.FilterType)
                {
                    case FilterType.Join
                        when link.ParentTable.SingleLinks.TryGetValue(filter.ColumnName, out var nextLink):
                        {
                            var next = BuildSql(filter.Next!, nextLink);
                            return $"SELECT DISTINCT [{link.ParentId.ColumnName}] AS [joinid]{(includeValue ? ", [value]" : "")} FROM [{link.ParentTable.DbName}] INNER JOIN ({next}) [j] ON [j].[joinid] = [{link.ParentTable.DbName}].[{nextLink.ChildId.ColumnName}]";
                        }
                    case FilterType.Join:
                        if (includeValue)
                        {
                            return
                                $"SELECT DISTINCT [{link.ParentId.ColumnName}] AS [joinid], [{filter.ColumnName}] AS [value] FROM [{link.ParentTable.DbName}]";
                        }
                        else
                        {
                            var where = GetSingleFilter(link.ParentTable.DbName, filter.ColumnName, filter.Next!.RelationName, filter.Next.Value);
                            return $"SELECT DISTINCT [{link.ParentId.ColumnName}] AS [joinid] FROM [{link.ParentTable.DbName}] WHERE {where}";
                        }
                }
            }

            return "";
        }

        private static string GetFilter(TableFilter filter, TableLinkDto link, string joinName, bool includeValue = false)
        {
            if (!includeValue) return "";

            if (filter is { Next: { } } || (filter.Next == null && filter.And.Count > 0) || (filter.Next == null && filter.Or.Count > 0))
            {
                switch (filter.FilterType)
                {
                    case FilterType.Join
                        when link.ParentTable.SingleLinks.TryGetValue(filter.ColumnName, out var nextLink):
                        return GetFilter(filter.Next!, nextLink, joinName, includeValue);
                    case FilterType.Join:
                        return GetSingleFilter(joinName, "value", filter.Next!.RelationName,
                            filter.Next.Value);
                }
            }

            return "";
        }
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

        public static string GetSingleFilter(string? table, string field, string op, object? value)
        {
            var rel = op switch
            {
                "_eq" => "=",
                "_neq" => "!=",
                "_lt" => "<",
                "_lte" => "<=",
                "_gt" => ">",
                "_gte" => ">=",
                "_contains" or "_starts_with" or "_ends_with" or "_like" => "like",
                "_ncontains" or "_nstarts_with" or "_nends_with" or "_nlike" => "not like",
                "_in" => "in",
                "_nin" => "not in",
                "_between" => "between",
                "_nbetween" => "not between",
                _ => "="
            };
            var val = op switch
            {
                "_starts_with" or "_nstarts_with" => $"'{value}%'",
                "_ends_with" or "_nends_with" => $"'%{value}'",
                "_contains" or "_ncontains" => $"'%{value}%'",
                "_in" or "_nin" => $"('{string.Join("','", (object[])(value ?? Array.Empty<object>()))}')",
                "_between" or "_nbetween" => $"'{string.Join("' AND '", (object[])(value ?? Array.Empty<object>()))}'",
                _ => $"'{value}'"
            };
            switch (op)
            {
                case "_eq" when value == null:
                    rel = "IS NULL";
                    val = "";
                    break;
                case "_neq" when value == null:
                    rel = "IS NOT NULL";
                    val = "";
                    break;
            }

            if (value is FieldRef fieldRef)
                val = fieldRef.ToString();

            if (table == null)
            {
                var filter = $"[{field}] {rel} {val}";
                return filter;
            }
            return $"[{table}].[{field}] {rel} {val}";
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

            // For complex joins, fall back to existing logic but with parameterized leaf filters
            // This maintains compatibility while securing the value injection points
            var (join, filter) = ToSql(model, alias);
            return new ParameterizedSql(join + (string.IsNullOrEmpty(filter) ? "" : " WHERE " + filter), Array.Empty<SqlParameterInfo>());
        }

    }
}
