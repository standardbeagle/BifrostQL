using GraphQLProxy.Schema;

namespace GraphQLProxy.QueryModel
{
    public sealed class TableFilter
    {
        public string? TableName { get; set; }
        public List<string> ColumnNames { get; set; } = null!;
        public string RelationName { get; set; } = null!;
        public object? Value { get; set; }

        public string ToSql(string? alias = null)
        {
            return DbFilterType.GetSingleFilter(alias ?? TableName, ColumnNames[0], RelationName, Value);
        }

        public static TableFilter? FromObject(object? value)
        {
            if (value == null) return null;
            var dictValue = value as Dictionary<string, object?>;
            if (dictValue == null) return null;
            var unwound = UnwindFilter(dictValue);
            if (unwound.keys.Count < 2) return null;

            var relation = unwound.keys.LastOrDefault() ?? "";

            return new TableFilter
            {
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
