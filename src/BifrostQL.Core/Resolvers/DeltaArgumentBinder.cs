using BifrostQL.Core.Modules;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Flattens the collection-diff save shape — <c>{ inserted: […], updated: […],
    /// deleted: […] }</c>, the natural output of a grid or sync diff — into the batch
    /// pipeline's action list, in inserted→updated→deleted order. Pure translation: one
    /// transaction, <c>batch-max-size</c>, <c>batch-duplicate-policy</c>, and the set-based
    /// bulk fast path all come from <see cref="BatchMutationPipeline"/> unchanged.
    /// </summary>
    internal static class DeltaArgumentBinder
    {
        public const string Inserted = "inserted";
        public const string Updated = "updated";
        public const string Deleted = "deleted";

        public static IReadOnlyList<BatchMutationPipeline.BatchAction> Bind(Dictionary<string, object?> delta)
        {
            var actions = new List<BatchMutationPipeline.BatchAction>();
            Append(actions, delta, Inserted, MutationAction.Insert);
            Append(actions, delta, Updated, MutationAction.Update);
            Append(actions, delta, Deleted, MutationAction.Delete);
            return actions;
        }

        private static void Append(
            List<BatchMutationPipeline.BatchAction> actions,
            Dictionary<string, object?> delta, string section, MutationAction action)
        {
            if (!delta.TryGetValue(section, out var value) || value is not IEnumerable<object?> rows)
                return;
            foreach (var row in rows)
                if (row is Dictionary<string, object?> data)
                    actions.Add(new BatchMutationPipeline.BatchAction(action, data));
        }
    }
}
