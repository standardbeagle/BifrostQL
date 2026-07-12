using System.Runtime.CompilerServices;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.History;

namespace BifrostQL.Core.Schema
{
    /// <summary>
    /// Single source of truth for the shape of the per-table trail read surface
    /// (<c>&lt;table&gt;History</c>). Schema emission (<see cref="TableSchemaGenerator"/>),
    /// resolver wiring (<c>BifrostDispatcher</c>), and model-load validation
    /// (<c>ModelConfigValidator</c>) all derive the field name and the resolved history
    /// target here, so the SDL, the wired resolver, and the collision check can never
    /// disagree about which field a history-enabled table generates or which table it
    /// reads.
    /// </summary>
    public static class HistorySurface
    {
        /// <summary>Root query field name for a tracked table's trail read.</summary>
        public static string HistoryFieldName(IDbTable trackedTable) => $"{trackedTable.GraphQlName}History";

        /// <summary>
        /// The history table a tracked table's trail read field targets, or null when
        /// the table generates no trail read field: it does not record history, its
        /// history config is invalid (already rejected at model load), or its resolved
        /// target does not exist in the model (ditto). Resolution reuses
        /// <see cref="HistoryConfig.ResolveTargetName"/> + <see cref="ModelTableReference"/> —
        /// the same rule the change-history writer uses — so the field always reads the
        /// very table the writer writes.
        /// </summary>
        public static IDbTable? ResolveReadTarget(IDbModel model, IDbTable trackedTable)
        {
            HistoryConfig config;
            try
            {
                config = HistoryConfig.FromTable(trackedTable);
            }
            catch (InvalidOperationException)
            {
                return null; // Invalid history tokens — ModelConfigValidator reports them.
            }

            if (!config.RecordsHistory)
                return null;

            var targetName = config.ResolveTargetName(model);
            if (targetName is null)
                return null; // No target configured — ModelConfigValidator reports it.

            return ModelTableReference.Find(model, targetName);
        }

        // The resolved history targets per model, cached because every intent-executor
        // request and every mutation pipeline entry asks, and the model (with its
        // metadata) is immutable after load. Keyed by model identity, so a reloaded
        // model computes fresh and the old entry falls away with the old model.
        private static readonly ConditionalWeakTable<IDbModel, IReadOnlySet<IDbTable>> TargetsByModel = new();

        /// <summary>
        /// Every table that is some history-enabled table's resolved history target —
        /// the model-level <c>history-table</c> default and all per-table overrides.
        /// These are system tables (epic decision D2): they are never published as
        /// ordinary root query fields, mutation fields, or join/link targets, and the
        /// intent executors reject them. Trail data is reachable ONLY through the
        /// generated <c>&lt;table&gt;History</c> fields, which force the entity
        /// discriminator, the tenant scope predicate, and the crypto image projection.
        /// </summary>
        public static IReadOnlySet<IDbTable> ResolveTargets(IDbModel model) =>
            TargetsByModel.GetValue(model, ComputeTargets);

        /// <summary>Whether <paramref name="table"/> is a resolved history target of <paramref name="model"/>.</summary>
        public static bool IsHistoryTarget(IDbModel model, IDbTable table) =>
            ResolveTargets(model).Contains(table);

        private static IReadOnlySet<IDbTable> ComputeTargets(IDbModel model)
        {
            var targets = new HashSet<IDbTable>(); // reference identity — model tables are singletons per model
            foreach (var table in model.Tables)
            {
                if (ResolveReadTarget(model, table) is { } target)
                    targets.Add(target);
            }
            return targets;
        }
    }
}
