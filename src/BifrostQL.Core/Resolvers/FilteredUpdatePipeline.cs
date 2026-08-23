using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using Microsoft.Extensions.DependencyInjection;
using static BifrostQL.Core.Resolvers.DbParameterBinder;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// The filtered set-update: <c>UPDATE … SET fieldset WHERE caller-filter AND
    /// transformer-row-scope</c> as ONE statement. Every condition a set-based write cannot
    /// honor is a fail-closed REJECTION — unlike the bulk batch path there is no per-row
    /// fallback here, and silently degrading a mass update would be worse than refusing it:
    ///
    /// <list type="bullet">
    /// <item>table not opted in (<see cref="FilteredUpdateConfig"/>) — re-checked here even
    /// though the SDL gate already hides the argument (a schema is not a security boundary);</item>
    /// <item>before-commit / in-transaction hooks registered (approval, history, CDC need
    /// per-row before-images and identities);</item>
    /// <item>state machine (per-row current-state validation) or optimistic-concurrency
    /// token (one client version cannot guard N rows, and a zero-affected filtered update is
    /// a legitimate outcome, not a CONFLICT);</item>
    /// <item>a WHERE referencing a column the caller may not read or filter on — the affected
    /// count is a MORE precise oracle than a result set, so the caller's filter columns clear
    /// the same <see cref="IColumnReadGuard"/>/<see cref="IColumnFilterGuard"/> set the read
    /// path enforces, and a denied column is an error, never silently stripped;</item>
    /// <item>an empty WHERE (a full-table update must be impossible to express by accident)
    /// or a relationship-traversing WHERE (v1 scope);</item>
    /// <item>more matching rows than <c>filtered-update-max-affected</c> — a COUNT precheck
    /// inside the update's own transaction throws and rolls back.</item>
    /// </list>
    ///
    /// The caller's filter NARROWS and never replaces: it is combined with the transformer
    /// chain's <c>AdditionalFilter</c> (tenant scope, policy row scope, soft-delete guard)
    /// via <see cref="TableFilter.CombineAnd"/>, and both render into one shared
    /// <see cref="SqlParameterCollection"/>. The result is always the affected-row COUNT.
    /// </summary>
    internal static class FilteredUpdatePipeline
    {
        public static async Task<int> UpdateByFilterAsync(
            IDbTable table, Dictionary<string, object?> setData, object? whereArg, MutationPipelineContext ctx)
        {
            if (!FilteredUpdateConfig.IsEnabled(table))
                throw new BifrostExecutionError(
                    $"Filtered update is not enabled for '{table.TableSchema}.{table.DbName}'. Set '{MetadataKeys.FilteredUpdate.Enabled}: {FilteredUpdateConfig.EnabledValue}' to opt in.");
            TableMutationPipeline.GuardNotHistoryTarget(table, ctx.Model);

            if (ctx.Services?.GetService<BeforeCommitMutationHooks>() is { IsEmpty: false } ||
                ctx.Services?.GetService<InTransactionMutationHooks>() is { IsEmpty: false })
                throw new BifrostExecutionError(
                    $"Filtered update of '{table.TableSchema}.{table.DbName}' is not available while mutation hooks (approval, history, CDC) are registered — they need per-row semantics. Use the batch mutation instead.");
            if (StateMachineConfigCollector.FromTable(table) is not null)
                throw new BifrostExecutionError(
                    $"Filtered update of '{table.TableSchema}.{table.DbName}' is not available on a state-machine table — transitions validate per row. Use the batch mutation instead.");
            if (!string.IsNullOrWhiteSpace(table.GetMetadataValue(MetadataKeys.Concurrency.Token)))
                throw new BifrostExecutionError(
                    $"Filtered update of '{table.TableSchema}.{table.DbName}' is not available on a concurrency-token table — one client version cannot guard many rows. Use the batch mutation instead.");

            if (setData.Count == 0)
                throw new BifrostExecutionError("Filtered update requires at least one column in 'set'.");
            foreach (var name in setData.Keys)
                if (IsPrimaryKeyColumn(table, name))
                    throw new BifrostExecutionError("Filtered update cannot set a primary-key column.");

            var userFilter = whereArg is null ? null : TableFilter.FromObject(whereArg, table.DbName);
            if (userFilter is null)
                throw new BifrostExecutionError(
                    "Filtered update requires a non-empty 'where'. A whole-table update must be written as an explicit filter.");

            AssertFilterColumnsAllowed(table, userFilter, ctx);

            var transformContext = new MutationTransformContext
            {
                Model = ctx.Model,
                UserContext = ctx.UserContext,
                Services = ctx.Services,
            };
            var transformResult = await ctx.Transformers.TransformAsync(
                table, MutationType.Update, new Dictionary<string, object?>(setData, StringComparer.OrdinalIgnoreCase), transformContext);
            transformResult.ThrowIfDenied();
            // Belt over the metadata gate above: any transformer demanding per-row semantics
            // that slipped past the static checks still rejects the set-based write.
            if (transformResult.ConflictOnNoRows || transformResult.StateTransition is not null)
                throw new BifrostExecutionError(
                    $"Filtered update of '{table.TableSchema}.{table.DbName}' is not available: a transformer requires per-row semantics. Use the batch mutation instead.");

            var dbData = ToDbColumnKeys(table, transformResult.Data);

            var combined = transformResult.AdditionalFilter is null
                ? userFilter
                : TableFilter.CombineAnd(userFilter, transformResult.AdditionalFilter);
            var dialect = ctx.ConnFactory.Dialect;
            var parameters = new SqlParameterCollection();
            var parts = combined.RenderParts(ctx.Model, dialect, parameters, alias: null);
            if (!string.IsNullOrWhiteSpace(parts.Joins))
                throw new BifrostExecutionError("Filtered update does not support relationship filters in 'where'.");
            if (string.IsNullOrWhiteSpace(parts.Where))
                throw new BifrostExecutionError("Filtered update requires a non-empty 'where'.");

            var maxAffected = FilteredUpdateConfig.MaxAffected(table);
            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var countSql = $"SELECT COUNT(*) FROM {tableRef} WHERE {parts.Where};";
            var updateSql = MutationCommandExecutor.BuildFilteredUpdateSql(dialect, table, tableRef, dbData.Keys, parts.Where);

            var affected = 0;
            await MutationCommandExecutor.RunInTransactionAsync(ctx.ConnFactory, async (conn, transaction) =>
            {
                // COUNT precheck inside the SAME transaction as the update, so the bound it
                // enforces is the bound the update sees.
                await using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = countSql;
                    countCmd.Transaction = transaction;
                    AddExtraParameters(countCmd, parameters.Parameters);
                    var matching = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ctx.CancellationToken));
                    if (matching > maxAffected)
                        throw new BifrostExecutionError(
                            $"Filtered update would affect {matching} rows, exceeding '{MetadataKeys.FilteredUpdate.MaxAffected}' ({maxAffected}). Narrow the filter or raise the cap.");
                }

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = updateSql;
                cmd.Transaction = transaction;
                AddParameters(cmd, dbData);
                AddExtraParameters(cmd, parameters.Parameters);
                affected = await cmd.ExecuteNonQueryAsync(ctx.CancellationToken);
            }, ctx.CancellationToken);

            // Post-commit observers get ONE aggregate notification: the fieldset written and
            // the DB-reported affected count as Result (invariant 8b — the count IS the value
            // here, there is no single key).
            await MutationNotifier.NotifyMutationAsync(
                ctx.Services, table, MutationType.Update, dbData, affected, ctx.UserContext);
            return affected;
        }

        /// <summary>
        /// The caller's WHERE columns clear the SAME column read/filter guards the read path
        /// enforces — collected with the same walker (<see cref="QueryTransformerService.CollectFilterColumns"/>),
        /// asserted per table, denial is an error, never a silent strip.
        /// </summary>
        private static void AssertFilterColumnsAllowed(IDbTable table, TableFilter userFilter, MutationPipelineContext ctx)
        {
            var filterTransformers = ctx.Services?.GetService<IFilterTransformers>();
            if (filterTransformers is null)
                return;
            var readGuards = filterTransformers.OfType<IColumnReadGuard>().ToArray();
            var filterGuards = filterTransformers.OfType<IColumnFilterGuard>().ToArray();
            if (readGuards.Length == 0 && filterGuards.Length == 0)
                return;

            var columnsByTable = new Dictionary<IDbTable, HashSet<string>>();
            QueryTransformerService.CollectFilterColumns(userFilter, table, (t, name) =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;
                if (!columnsByTable.TryGetValue(t, out var set))
                    columnsByTable[t] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(name);
            });

            var guardContext = new QueryTransformContext
            {
                Model = ctx.Model,
                UserContext = ctx.UserContext,
                QueryType = QueryType.Standard,
            };
            foreach (var (guardTable, columns) in columnsByTable)
            {
                if (columns.Count == 0)
                    continue;
                var names = columns.ToArray();
                foreach (var guard in readGuards)
                    guard.AssertColumnsReadable(guardTable, names, guardContext);
                foreach (var guard in filterGuards)
                    guard.AssertColumnsFilterable(guardTable, names, guardContext);
            }
        }
    }
}
