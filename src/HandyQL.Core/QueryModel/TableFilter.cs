using GraphQLProxy.Model;
using GraphQLProxy.Schema;

namespace GraphQLProxy.QueryModel
{
    public sealed class TableFilter
    {
        private TableFilter() { }
        public string TableName { get; init; } = null!;
        public List<string> ColumnNames { get; set; } = null!;
        public string RelationName { get; set; } = null!;
        public object? Value { get; set; }

        public (string join, string comparison) ToSql(IDbModel model, string? alias = null)
        {
            if (ColumnNames.Count == 1)
            {
                return ("", DbFilterType.GetSingleFilter(alias ?? TableName, ColumnNames[0], RelationName, Value));
            }
            var join = "";
            var table = model.GetTableFromTableName(TableName);
            var links = new List<TableLinkDto>();
            var linkTable = table;
            foreach(var column in ColumnNames.SkipLast(1))
            {
                var link = linkTable.SingleLinks[column];
                links.Add(link);
                linkTable = link.ParentTable;
            }
            for(int i = links.Count-1; i >= 0; i--)
            {
                var link = links[i];
                if (join == "")
                {
                    var where = DbFilterType.GetSingleFilter(link.ParentTable.TableName, ColumnNames[i + 1], RelationName, Value);
                    join = $"SELECT DISTINCT [{link.ParentId.ColumnName}] AS joinid FROM [{link.ParentTable.TableName}] WHERE {where}";
                } else
                {
                    var parentTable = link.ParentTable.TableName;
                    var previousLink = links[i+1];
                    join = $"SELECT DISTINCT [{link.ParentId.ColumnName}] AS joinid FROM [{parentTable}] INNER JOIN ({join}) j ON j.joinid = [{parentTable}].[{previousLink.ChildId.ColumnName}]";
                }
            }
            join = $" INNER JOIN ({join}) j ON j.joinid = [{alias ?? table.TableName}].[{links[0].ChildId.ColumnName}]";
            return (join, "");
        }

        public static TableFilter? FromObject(object? value, string tableName)
        {
            if (value == null) return null;
            var dictValue = value as Dictionary<string, object?>;
            if (dictValue == null) return null;
            var unwound = UnwindFilter(dictValue);
            if (unwound.keys.Count < 2) return null;

            var relation = unwound.keys.LastOrDefault() ?? "";

            return new TableFilter
            {
                TableName= tableName,
                ColumnNames = unwound.keys.SkipLast(1).ToList(),
                RelationName = relation,
                Value = unwound.value
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
    }
}
