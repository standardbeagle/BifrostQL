using System.Globalization;
using System.Text;
using BifrostQL.Core.Model;
using static BifrostQL.Core.Resolvers.DbParameterBinder;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Resolves duplicate-key actions in a batch DETERMINISTICALLY, in Core, before any SQL is
    /// built — on both the per-row and set-based paths, so the database never sees two actions
    /// addressing one row and engine join semantics never decide the outcome. The resolution is
    /// the table's <see cref="MetadataKeys.Batch.DuplicatePolicy"/>:
    ///
    /// <list type="bullet">
    /// <item><b>last-wins</b> (default) — collapse duplicates to their sequential net effect,
    /// exactly what applying the actions one-by-one would leave behind: update+update merges per
    /// column with the later action winning; update-then-delete and delete-then-update both net
    /// to the delete (a deleted row's update matches nothing); delete+delete is one delete. The
    /// survivor takes the LAST occurrence's position, so ordering stays deterministic.</item>
    /// <item><b>reject</b> — a duplicate key is a clean <see cref="BifrostExecutionError"/> and
    /// the batch executes nothing.</item>
    /// </list>
    ///
    /// Row identity is static: the action's primary-key values (updates, upserts, and PK-keyed
    /// deletes), or the full predicate for a non-PK delete (which therefore only collapses
    /// against an IDENTICAL predicate — a predicate's runtime overlap with a PK row is
    /// undecidable here). A collision involving an UPSERT is refused under both policies: its
    /// net effect depends on row existence, so no deterministic collapse exists. Inserts carry
    /// no prior row identity and pass through (a duplicate inserted PK is the database
    /// constraint's error, identically on every path).
    /// </summary>
    internal static class BatchDuplicateNormalizer
    {
        internal const string LastWins = "last-wins";
        internal const string Reject = "reject";

        public static IReadOnlyList<BatchMutationPipeline.BatchAction> Normalize(
            IDbTable table, IReadOnlyList<BatchMutationPipeline.BatchAction> actions)
        {
            var policy = ResolvePolicy(table);

            // (order, action) survivors; bySignature points at the survivor slot for a key.
            var survivors = new List<(int Order, BatchMutationPipeline.BatchAction Action)>(actions.Count);
            var bySignature = new Dictionary<string, int>(StringComparer.Ordinal);
            var anyCollapsed = false;

            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                var signature = Signature(table, action);
                if (signature is null || !bySignature.TryGetValue(signature, out var slot))
                {
                    if (signature is not null)
                        bySignature[signature] = survivors.Count;
                    survivors.Add((i, action));
                    continue;
                }

                var existing = survivors[slot].Action;
                if (existing.Action == MutationAction.Upsert || action.Action == MutationAction.Upsert)
                    throw new BifrostExecutionError(
                        $"Batch for '{table.TableSchema}.{table.DbName}' contains an upsert and another action for the same key; " +
                        "an upsert's net effect depends on row existence, so the duplicate cannot be collapsed deterministically. Split the batch.");

                if (policy == Reject)
                    throw new BifrostExecutionError(
                        $"Batch for '{table.TableSchema}.{table.DbName}' contains multiple actions for the same key " +
                        $"and '{MetadataKeys.Batch.DuplicatePolicy}' is '{Reject}'.");

                anyCollapsed = true;
                survivors[slot] = (i, Collapse(existing, action));
            }

            if (!anyCollapsed)
                return actions;
            return survivors.OrderBy(s => s.Order).Select(s => s.Action).ToList();
        }

        /// <summary>The sequential net effect of <paramref name="later"/> applied after <paramref name="earlier"/> on one row.</summary>
        private static BatchMutationPipeline.BatchAction Collapse(
            BatchMutationPipeline.BatchAction earlier, BatchMutationPipeline.BatchAction later)
        {
            // A delete on either side wins: an earlier delete makes the later update match
            // nothing; a later delete makes the earlier update moot.
            if (earlier.Action == MutationAction.Delete)
                return earlier;
            if (later.Action == MutationAction.Delete)
                return later;

            // update + update: per-column merge, the later action's columns overwrite.
            var merged = new Dictionary<string, object?>(earlier.Data, StringComparer.OrdinalIgnoreCase);
            foreach (var (column, value) in later.Data)
                merged[column] = value;
            return new BatchMutationPipeline.BatchAction(MutationAction.Update, merged);
        }

        /// <summary>
        /// The action's static row identity, or null when it has none (inserts; updates missing
        /// part of the key, which the executors skip downstream anyway). PK identities and
        /// non-PK delete predicates use disjoint prefixes so they can never collide.
        /// </summary>
        private static string? Signature(IDbTable table, BatchMutationPipeline.BatchAction action)
        {
            if (action.Action == MutationAction.Insert)
                return null;

            var keyColumns = table.KeyColumns.Select(c => c.ColumnName).ToList();
            var pkValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in action.Data)
                if (IsPrimaryKeyColumn(table, name))
                    pkValues[ToDbColumnName(table, name)] = value;

            if (action.Action == MutationAction.Delete && !(pkValues.Count == keyColumns.Count && action.Data.Count == keyColumns.Count))
            {
                // Predicate delete: identity is the exact predicate, nothing else.
                return "pred|" + Render(action.Data.ToDictionary(kv => ToDbColumnName(table, kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase));
            }

            if (pkValues.Count != keyColumns.Count)
                return null;
            return "pk|" + Render(pkValues);
        }

        private static string Render(IReadOnlyDictionary<string, object?> values)
        {
            var sb = new StringBuilder();
            foreach (var column in values.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                sb.Append(column.ToLowerInvariant()).Append('=')
                  .Append(Convert.ToString(values[column], CultureInfo.InvariantCulture)).Append('|');
            return sb.ToString();
        }

        private static string ResolvePolicy(IDbTable table)
        {
            var raw = table.GetMetadataValue(MetadataKeys.Batch.DuplicatePolicy);
            if (string.IsNullOrWhiteSpace(raw))
                return LastWins;
            var policy = raw.Trim().ToLowerInvariant();
            if (policy is LastWins or Reject)
                return policy;
            throw new InvalidOperationException(
                $"Metadata '{MetadataKeys.Batch.DuplicatePolicy}' must be '{LastWins}' or '{Reject}', but was '{raw}'.");
        }
    }
}
