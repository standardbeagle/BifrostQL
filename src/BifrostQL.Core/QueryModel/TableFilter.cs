using BifrostQL.Core.Model;

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
        public List<TableFilter> And { get; init; } = new List<TableFilter>();
        public List<TableFilter> Or { get; init; } = new List<TableFilter>();

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
                throw new ArgumentOutOfRangeException("value", "object must have two values");
            }
            var table = model.GetTableFromDbName(TableName ?? throw new InvalidDataException("TableFilter with undefined TableName"));
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
            var dictValue = value as Dictionary<string, object?> ?? throw new ArgumentNullException(nameof(value));

            var filter = StackFilters(dictValue, tableName);
            if (filter.And.Count == 0 && filter.Or.Count == 0 && filter.Next == null)
                throw new ArgumentException("Invalid filter object", nameof(value));
            return filter;
        }

        private static TableFilter StackFilters(IDictionary<string, object?>? filter, string? tableName)
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter));
            var kv = filter.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(kv.Key)) throw new ArgumentOutOfRangeException(nameof(filter));
            return kv switch
            {
                { Key: "and" } => new TableFilter
                {
                    And = ((IEnumerable<object?>)kv.Value!).Select(v => StackFilters((IDictionary<string, object?>?)v, tableName)).ToList(),
                    FilterType = FilterType.And,
                },
                { Key: "or" } => new TableFilter
                {
                    Or = ((IEnumerable<object?>)kv.Value!).Select(v => StackFilters((IDictionary<string, object?>?)v, tableName)).ToList(),
                    FilterType = FilterType.Or,
                },
                _ when kv.Value is IDictionary<string, object?> val => new TableFilter
                {
                    ColumnName = kv.Key,
                    Next = StackFilters(val, null),
                    TableName = tableName,
                    FilterType = FilterType.Join,
                },
                _ when kv.Value == null && kv.Key == null => throw new ArgumentNullException(),
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
            if (op == "_eq" && value == null)
            {
                rel = "IS NULL";
                val = "";
            }
            if (op == "_neq" && value == null)
            {
                rel = "IS NOT NULL";
                val = "";
            }

            if (value is FieldRef fieldRef) 
                val = fieldRef.ToString();

            if (table == null)
            {
                string filter = $"[{field}] {rel} {val}";
                return filter;
            }
            return $"[{table}].[{field}] {rel} {val}";
        }

    }
}
