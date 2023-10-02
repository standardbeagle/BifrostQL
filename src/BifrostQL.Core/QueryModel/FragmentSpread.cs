namespace BifrostQL.Core.QueryModel
{
    public sealed class FragmentSpread
    {
        public string FragmentName { get; init; } = null!;
        public GqlObjectQuery? Table { get; set; }

    }
}
