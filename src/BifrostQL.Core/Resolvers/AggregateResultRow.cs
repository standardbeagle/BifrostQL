namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// One group row returned by a <c>&lt;table&gt;Aggregate</c> query. Group-key
    /// values are keyed by column GraphQL name; <see cref="Count"/> backs
    /// <c>_count</c> (null when not selected); <see cref="Ops"/> holds the
    /// per-op-group value objects (<c>_sum</c>, <c>_avg</c>, …), each present only
    /// when that op was selected. Read by <see cref="AggregateFieldResolver"/>.
    /// </summary>
    public sealed class AggregateResultRow
    {
        public required IReadOnlyDictionary<string, object?> GroupValues { get; init; }
        public required int? Count { get; init; }
        public required IReadOnlyDictionary<string, AggregateFields> Ops { get; init; }
    }

    /// <summary>
    /// The payload of one op group (e.g. <c>_sum { total }</c>): aggregate values
    /// keyed by column GraphQL name.
    /// </summary>
    public sealed class AggregateFields
    {
        public required IReadOnlyDictionary<string, object?> Values { get; init; }
    }
}
