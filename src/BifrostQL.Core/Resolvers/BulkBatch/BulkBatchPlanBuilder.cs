using System.Globalization;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static BifrostQL.Core.Resolvers.DbParameterBinder;

namespace BifrostQL.Core.Resolvers.BulkBatch
{
    /// <summary>
    /// Decides whether a batch may take the dialect's set-based fast path and, when it may,
    /// runs the FULL per-row mutation transformer chain and flattens the results into a
    /// <see cref="BulkBatchPlan"/>. Every condition the set-based statements cannot honor —
    /// registered in-transaction hooks (approval/history/CDC need per-row before-images and
    /// identities), upserts (per-row existence probe), state machines (per-row current-row
    /// load), heterogeneous transformer filters — returns null so the caller keeps the
    /// per-row pipeline: falling back is normal operation, never an error.
    /// </summary>
    internal static class BulkBatchPlanBuilder
    {
        internal const int DefaultBulkThreshold = 50;
        internal const int MaxOpGroups = 8;

        /// <summary>The observer-facing shadow of one staged action, indexed by staging seq.</summary>
        internal sealed record PendingOutcome(int Seq, MutationType MutationType, IDictionary<string, object?> Data);

        internal sealed record BuiltBulkBatch(BulkBatchPlan Plan, IReadOnlyList<PendingOutcome> Outcomes);

        /// <summary>
        /// The table's bulk fast-path threshold (<see cref="MetadataKeys.Batch.BulkThreshold"/>,
        /// default 50). Unlike batch-max-size, zero or a negative value is legal and DISABLES
        /// the fast path for the table; a non-integer still fails fast.
        /// </summary>
        internal static int GetBulkThreshold(IDbTable table)
        {
            var raw = table.GetMetadataValue(MetadataKeys.Batch.BulkThreshold);
            if (string.IsNullOrWhiteSpace(raw))
                return DefaultBulkThreshold;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            throw new InvalidOperationException(
                $"Metadata '{MetadataKeys.Batch.BulkThreshold}' must be an integer, but was '{raw}'.");
        }

        /// <summary>
        /// Runs the transformer chain per row and builds the set-based plan, or returns null
        /// (with the reason logged at debug) when any gating condition fails. Transformers are
        /// pure over their inputs, so a null here is safe: the per-row path re-transforms the
        /// original action data.
        /// </summary>
        public static async Task<BuiltBulkBatch?> TryBuildAsync(
            IDbTable table,
            IReadOnlyList<BatchMutationPipeline.BatchAction> actions,
            MutationPipelineContext ctx,
            MutationTransformContext transformContext)
        {
            var logger = ctx.Services?.GetService<ILoggerFactory>()?.CreateLogger("BifrostQL.BulkBatch");

            if (!IsEligible(table, actions, ctx, out var reason))
            {
                logger?.LogDebug("Bulk batch fast path skipped for {Table}: {Reason}", table.DbName, reason);
                return null;
            }

            var dialect = ctx.ConnFactory.Dialect;
            var rows = new List<BulkStagedAction>(actions.Count);
            var outcomes = new List<PendingOutcome>(actions.Count);
            var groups = new Dictionary<string, (BulkOpGroup Group, int Id)>();
            // Rendered transformer filters all carry the same @p0.. parameter names, so every
            // statement of the batch must share ONE rendered filter (or none): the first
            // non-empty rendering is canonical and any later mismatch aborts the fast path.
            (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters)? canonicalFilter = null;
            var seq = 0;

            foreach (var action in actions)
            {
                var staged = action.Action switch
                {
                    MutationAction.Insert => await StageInsert(table, action.Data, ctx, transformContext),
                    MutationAction.Update => await StageUpdate(table, action.Data, ctx, transformContext, dialect),
                    MutationAction.Delete => await StageDelete(table, action.Data, ctx, transformContext, dialect),
                    _ => throw new InvalidOperationException($"Unexpected bulk action '{action.Action}'."),
                };
                if (staged is null)
                    continue; // the per-row path skips these rows too (empty data, missing key/set columns)

                var (op, setColumns, keyColumns, values, filter, conflict, mutationType, observerData) = staged;

                if (filter.WhereSuffix.Length > 0)
                {
                    if (canonicalFilter is null)
                        canonicalFilter = filter;
                    else if (!FiltersMatch(canonicalFilter.Value, filter))
                    {
                        logger?.LogDebug("Bulk batch fast path skipped for {Table}: heterogeneous transformer filters", table.DbName);
                        return null;
                    }
                }

                var signature = $"{op}|{string.Join(",", setColumns)}|{string.Join(",", keyColumns)}|{filter.WhereSuffix}";
                if (!groups.TryGetValue(signature, out var group))
                {
                    if (groups.Count == MaxOpGroups)
                    {
                        logger?.LogDebug("Bulk batch fast path skipped for {Table}: more than {Max} column-signature groups", table.DbName, MaxOpGroups);
                        return null;
                    }
                    group = (new BulkOpGroup(op, groups.Count, setColumns, keyColumns, filter.WhereSuffix, filter.Parameters), groups.Count);
                    groups[signature] = group;
                }

                rows.Add(new BulkStagedAction(seq, op, group.Id, conflict, values));
                outcomes.Add(new PendingOutcome(seq, mutationType, observerData));
                seq++;
            }

            if (rows.Count == 0)
                return null;

            var stagingColumns = rows
                .SelectMany(r => r.Values.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var plan = new BulkBatchPlan(
                table.TableSchema, table.DbName, stagingColumns, rows,
                groups.Values.OrderBy(g => g.Id).Select(g => g.Group).ToList());
            return new BuiltBulkBatch(plan, outcomes);
        }

        internal static bool IsEligible(
            IDbTable table,
            IReadOnlyList<BatchMutationPipeline.BatchAction> actions,
            MutationPipelineContext ctx,
            out string reason)
        {
            var threshold = GetBulkThreshold(table);
            if (threshold <= 0)
            {
                reason = "bulk-batch-threshold disables the fast path";
                return false;
            }
            if (actions.Count < threshold)
            {
                reason = $"batch of {actions.Count} is below bulk-batch-threshold {threshold}";
                return false;
            }
            if (actions.Any(a => a.Action == MutationAction.Upsert))
            {
                reason = "batch contains an upsert (needs the per-row existence probe)";
                return false;
            }
            if (StateMachineConfigCollector.FromTable(table) is not null)
            {
                reason = "table has a state machine (needs the per-row current-row load)";
                return false;
            }
            if (ctx.Services?.GetService<BeforeCommitMutationHooks>() is { IsEmpty: false })
            {
                reason = "before-commit mutation hooks are registered";
                return false;
            }
            if (ctx.Services?.GetService<InTransactionMutationHooks>() is { IsEmpty: false })
            {
                reason = "in-transaction mutation hooks are registered";
                return false;
            }
            reason = "";
            return true;
        }

        private static bool FiltersMatch(
            (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) canonical,
            (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) candidate)
        {
            if (!string.Equals(canonical.WhereSuffix, candidate.WhereSuffix, StringComparison.Ordinal))
                return false;
            if (canonical.Parameters.Count != candidate.Parameters.Count)
                return false;
            for (var i = 0; i < canonical.Parameters.Count; i++)
            {
                if (!canonical.Parameters[i].Equals(candidate.Parameters[i]))
                    return false;
            }
            return true;
        }

        private sealed record StagedRowParts(
            BulkOpCode Op,
            IReadOnlyList<string> SetColumns,
            IReadOnlyList<string> KeyColumns,
            IReadOnlyDictionary<string, object?> Values,
            (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) Filter,
            bool ConflictOnNoRows,
            MutationType MutationType,
            IDictionary<string, object?> ObserverData);

        private static async Task<StagedRowParts?> StageInsert(
            IDbTable table, Dictionary<string, object?> data,
            MutationPipelineContext ctx, MutationTransformContext transformContext)
        {
            if (data.Count == 0) return null;
            var transformResult = await ctx.Transformers.TransformAsync(table, MutationType.Insert, data, transformContext);
            transformResult.ThrowIfDenied();
            var dbData = ToDbColumnKeys(table, transformResult.Data);
            var columns = dbData.Keys.ToList();
            return new StagedRowParts(
                BulkOpCode.Insert, columns, Array.Empty<string>(), dbData,
                ("", Array.Empty<SqlParameterInfo>()), ConflictOnNoRows: false,
                MutationType.Insert, dbData);
        }

        private static async Task<StagedRowParts?> StageUpdate(
            IDbTable table, Dictionary<string, object?> data,
            MutationPipelineContext ctx, MutationTransformContext transformContext, ISqlDialect dialect)
        {
            if (data.Count == 0) return null;
            var caseData = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
            var keyData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in caseData.Where(d => IsPrimaryKeyColumn(table, d.Key)))
                keyData[ToDbColumnName(table, d.Key)] = d.Value;
            if (keyData.Count == 0 || caseData.Count == keyData.Count) return null;

            // No CurrentRow: state-machine tables were gated off, and only they read it.
            var transformResult = await ctx.Transformers.TransformAsync(table, MutationType.Update, caseData, transformContext);
            transformResult.ThrowIfDenied();
            var filter = MutationCommandExecutor.RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

            var updatedData = ToDbColumnKeys(table, transformResult.Data);
            var setColumns = updatedData.Keys.Where(k => !keyData.ContainsKey(k)).ToList();
            if (setColumns.Count == 0) return null;

            return new StagedRowParts(
                BulkOpCode.Update, setColumns, keyData.Keys.ToList(), updatedData,
                filter, transformResult.ConflictOnNoRows, MutationType.Update, updatedData);
        }

        private static async Task<StagedRowParts?> StageDelete(
            IDbTable table, Dictionary<string, object?> data,
            MutationPipelineContext ctx, MutationTransformContext transformContext, ISqlDialect dialect)
        {
            if (data.Count == 0) return null;
            // Thread the captured module arguments (e.g. _hardDelete) exactly as the per-row
            // path does, so the soft-delete transformer sees the same rewrite decision.
            var deleteTransformContext = ctx.ModuleArguments.Count == 0
                ? transformContext
                : new MutationTransformContext
                {
                    Model = transformContext.Model,
                    UserContext = transformContext.UserContext,
                    Services = transformContext.Services,
                    ModuleArguments = ctx.ModuleArguments,
                };

            var transformResult = await ctx.Transformers.TransformAsync(table, MutationType.Delete, data, deleteTransformContext);
            transformResult.ThrowIfDenied();
            var filter = MutationCommandExecutor.RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);
            var dbData = ToDbColumnKeys(table, transformResult.Data);

            if (transformResult.MutationType == MutationType.Update)
            {
                // Soft-delete rewrite: key columns scope the WHERE; the transformer-stamped
                // deleted_at/deleted_by columns become the SET. Mirrors the per-row branch,
                // including surfacing the PRE-rekey data to observers.
                var keyColumns = dbData.Keys.Where(k => IsPrimaryKeyColumn(table, k)).ToList();
                var setColumns = dbData.Keys.Where(k => !IsPrimaryKeyColumn(table, k)).ToList();
                if (keyColumns.Count == 0 || setColumns.Count == 0) return null;
                return new StagedRowParts(
                    BulkOpCode.Update, setColumns, keyColumns, dbData,
                    filter, ConflictOnNoRows: false, MutationType.Update, transformResult.Data);
            }

            // Hard delete: EVERY data column is a WHERE predicate, matching BuildDeleteSql.
            return new StagedRowParts(
                BulkOpCode.Delete, Array.Empty<string>(), dbData.Keys.ToList(), dbData,
                filter, ConflictOnNoRows: false, MutationType.Delete, dbData);
        }
    }
}
