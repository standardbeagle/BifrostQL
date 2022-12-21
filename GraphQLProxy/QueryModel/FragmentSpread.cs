namespace GraphQLProxy.QueryModel
{
    public sealed class FragmentSpread
    {
        public string FragmentName { get; init; } = null!;
        public TableSqlData? Table { get; set; }

    }
}
