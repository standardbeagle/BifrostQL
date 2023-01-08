using GraphQLProxy.Schema;

namespace GraphQLProxy.QueryModel
{
    public sealed class TableFilter
    {
        public string? TableName { get; set; }
        public string ColumnName { get; set; } = null!;
        public string RelationName { get; set; } = null!;
        public object? Value { get; set; }

        public string ToSql(string? alias = null)
        {
            return DbFilterType.GetSingleFilter(alias ?? TableName, ColumnName, RelationName, Value);
        }

        public static TableFilter? FromObject(object? value)
        {
            if (value == null) return null;
            var columnRow = (value as Dictionary<string, object?>)?.FirstOrDefault();
            if (columnRow == null) return null;
            var operationRow = (columnRow?.Value as Dictionary<string, object?>)?.FirstOrDefault();
            if (operationRow == null) return null;
            return new TableFilter
            {
                ColumnName = columnRow?.Key!,
                RelationName = operationRow?.Key!,
                Value = operationRow?.Value
            };
        }
    }
}
