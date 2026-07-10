using BifrostQL.Core.Schema;
using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Field resolver for the aggregate output types (<c>&lt;table&gt;_aggregate</c>
    /// and <c>&lt;table&gt;_aggregateFields</c>). Reads a field off the plain-data
    /// source produced by <see cref="AggregateTableResolver"/> — an
    /// <see cref="AggregateResultRow"/> (group key, <c>_count</c>, or an op group)
    /// or an <see cref="AggregateFields"/> (a value column). Stateless singleton;
    /// wired directly onto the aggregate type fields so it bypasses the join
    /// dispatcher (whose source contract is row/lookup readers, not these objects).
    /// </summary>
    public sealed class AggregateFieldResolver : IFieldResolver
    {
        public static readonly AggregateFieldResolver Instance = new();

        private AggregateFieldResolver() { }

        public ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var name = context.FieldDefinition.Name;
            return context.Source switch
            {
                AggregateResultRow row => ValueTask.FromResult(ResolveRow(row, name)),
                AggregateFields fields => ValueTask.FromResult(fields.Values.GetValueOrDefault(name)),
                _ => throw new BifrostExecutionError(
                    $"Aggregate field '{name}' has no data for source type {context.Source?.GetType().FullName ?? "null"}."),
            };
        }

        private static object? ResolveRow(AggregateResultRow row, string name)
        {
            if (name == AggregateSurface.CountField)
                return row.Count;
            if (row.Ops.TryGetValue(name, out var opFields))
                return opFields;
            return row.GroupValues.GetValueOrDefault(name);
        }
    }
}
