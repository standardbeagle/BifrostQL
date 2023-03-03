using BifrostQL.Model;
using BifrostQL.Schema;
using System.Collections.Generic;

namespace BifrostQL.QueryModel
{
    public sealed class TableFilter
    {
        private TableFilter() { }
        public string? TableName { get; init; }
        //Handles multiple table references
        public List<string> ColumnNames { get; set; } = null!;
        public string ColumnName { get; init; } = null!;
        public string RelationName { get; set; } = null!;
        public object? Value { get; set; }
        public TableFilter? Next { get; set; }
        public List<TableFilter> And { get; init; } = new List<TableFilter>();
        public List<TableFilter> Or { get; init; } = new List<TableFilter>();

        public (string join, string comparison) ToSql(IDbModel model, string? alias = null)
        {
            if (ColumnNames.Count == 1)
            {
                return ("", GetSingleFilter(alias ?? TableName, ColumnNames[0], RelationName, Value));
            }
            var join = "";
            var table = model.GetTableFromTableName(TableName ?? throw new InvalidDataException("TableFilter with undefined TableName"));
            var links = new List<TableLinkDto>();
            var linkTable = table;
            foreach (var column in ColumnNames.SkipLast(1))
            {
                var link = linkTable.SingleLinks[column];
                links.Add(link);
                linkTable = link.ParentTable;
            }

            for (int i = links.Count - 1; i >= 0; i--)
            {
                var link = links[i];
                if (join == "")
                {
                    var where = GetSingleFilter(link.ParentTable.DbName, ColumnNames[i + 1], RelationName, Value);
                    join = $"SELECT DISTINCT [{link.ParentId.ColumnName}] AS joinid FROM [{link.ParentTable.DbName}] WHERE {where}";
                }
                else
                {
                    var parentTable = link.ParentTable.DbName;
                    var previousLink = links[i + 1];
                    join = $"SELECT DISTINCT [{link.ParentId.ColumnName}] AS joinid FROM [{parentTable}] INNER JOIN ({join}) j ON j.joinid = [{parentTable}].[{previousLink.ChildId.ColumnName}]";
                }
            }
            join = $" INNER JOIN ({join}) j ON j.joinid = [{alias ?? table.DbName}].[{links[0].ChildId.ColumnName}]";
            return (join, "");
        }

        public static TableFilter FromObject(object? value, string tableName)
        {
            var dictValue = value as Dictionary<string, object?> ?? throw new ArgumentNullException(nameof(value));

            //var filter = StackFilters(dictValue, tableName);

            var unwound = UnwindFilter(dictValue);
            if (unwound.keys.Count < 2) throw new ArgumentOutOfRangeException(nameof(value), $"object must have two values");

            var relation = unwound.keys.LastOrDefault() ?? throw new ArgumentOutOfRangeException(nameof(value), "relation must be specified");

            return new TableFilter
            {
                TableName = tableName,
                ColumnNames = unwound.keys.SkipLast(1).ToList(),
                RelationName = relation,
                Value = unwound.value
            };
        }

        private static TableFilter StackFilters(IDictionary<string, object?> filter, string? tableName)
        {
            var kv = filter?.FirstOrDefault() ?? throw new ArgumentNullException();
            return kv switch
            {
                { Key: "and" } => new TableFilter
                {
                    And = ((IEnumerable<IDictionary<string, object?>>)kv.Value!).Select(v => StackFilters(v, tableName)).ToList(),
                },
                { Key: "or" } => new TableFilter
                {
                    Or = ((IEnumerable<IDictionary<string, object?>>)kv.Value!).Select(v => StackFilters(v, tableName)).ToList(),
                },
                _ when kv.Value is IDictionary<string, object?> val => new TableFilter
                {
                    ColumnName = kv.Key,
                    Next = StackFilters(val, null),
                    TableName = tableName,
                },
                _ => new TableFilter
                {
                    RelationName = kv.Key,
                    Value = kv.Value,
                },
            };
        }

        private static (List<string> keys, object? value) UnwindFilter(IDictionary<string, object?> filter)
        {
            var kv = filter?.FirstOrDefault();
            if (kv == null)
                return (new List<string>(), null);
            if (kv.Value.Value is IDictionary<string, object?> subValue)
            {
                var unwoundValue = UnwindFilter(subValue);
                unwoundValue.keys.Insert(0, kv.Value.Key);
                return unwoundValue;
            }
            return (new List<string>() { kv.Value.Key }, kv.Value.Value);
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
            if (op == "_eq" && val == null)
            {
                rel = "IS NULL";
                val = "";
            }
            if (op == "_neq" && val == null)
            {
                rel = "IS NOT NULL";
                val = "";
            }
            if (table == null)
            {
                string filter = $"[{field}] {rel} {val}";
                return filter;
            }
            return $"[{table}].[{field}] {rel} {val}";
        }

    }
}
