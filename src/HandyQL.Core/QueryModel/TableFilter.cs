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
    }
}
