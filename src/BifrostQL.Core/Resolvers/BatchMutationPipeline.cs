using System.Data.Common;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Approval;
using BifrostQL.Core.QueryModel;
using Microsoft.Extensions.DependencyInjection;
using static BifrostQL.Core.Resolvers.DbParameterBinder;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// The shared multi-action batch mutation seam: ALL actions of a batch execute on
    /// one connection inside ONE transaction — transformer chain, before-commit and
    /// after-write in-transaction hooks per action — and commit or roll back as a
    /// unit; a veto anywhere writes nothing. Extracted from
    /// <see cref="DbTableBatchResolver"/> so the GraphQL batch field and the
    /// protocol-adapter batch-intent path (<see cref="MutationIntentExecutor"/>) run
    /// ONE pipeline, mirroring how <see cref="TableMutationPipeline"/> is shared for
    /// single-row writes — transformer application lives inside these methods, so no
    /// caller has an API surface that reaches SQL without it.
    /// </summary>
    internal static class BatchMutationPipeline
    {
        internal const int DefaultMaxBatchSize = 100;

        /// <summary>One parsed batch action: the verb plus its data dictionary.</summary>
        internal readonly record struct BatchAction(MutationAction Action, Dictionary<string, object?> Data);

        internal sealed record BatchActionOutcome(
            int Affected,
            MutationType MutationType,
            IDictionary<string, object?> Data,
            StateTransitionInfo? Transition,
            // Set when the approval gate diverted this action into a pending change: it
            // applied nothing (Affected 0) and no observer should fire for it; the batch
            // surfaces the pending-approval message after it commits the pending rows.
            string? PendingApproval = null);

        /// <summary>
        /// The table's maximum batch size (<see cref="MetadataKeys.Batch.MaxSize"/>,
        /// default 100), enforced on every batch entry point.
        /// </summary>
        internal static int GetMaxBatchSize(IDbTable table)
            => Utils.MetadataNumber.PositiveInt(
                table.GetMetadataValue(MetadataKeys.Batch.MaxSize), DefaultMaxBatchSize, MetadataKeys.Batch.MaxSize);

        /// <summary>
        /// Executes the batch inside one transaction and returns the total affected
        /// row count. Post-commit observers fire only after the commit, so audit and
        /// state-transition notifications never describe rolled-back work.
        /// </summary>
        public static async Task<int> ExecuteBatchAsync(
            IDbTable table, IReadOnlyList<BatchAction> actions, MutationPipelineContext ctx)
        {
            TableMutationPipeline.GuardNotHistoryTarget(table, ctx.Model);

            if (actions.Count == 0)
                return 0;

            var maxBatchSize = GetMaxBatchSize(table);
            if (actions.Count > maxBatchSize)
                throw new BifrostExecutionError(
                    $"Batch size {actions.Count} exceeds maximum allowed size of {maxBatchSize}.");

            var ct = ctx.CancellationToken;
            var transformContext = new MutationTransformContext
            {
                Model = ctx.Model,
                UserContext = ctx.UserContext,
                Services = ctx.Services,
            };

            // Set-based fast path: on a provider with a bulk executor, an eligible batch is
            // transformed per row up front, staged, and applied by set-based DML inside one
            // inline SQL transaction. A null plan (or a staging-phase failure, which cannot
            // have written anything) falls through to the per-row loop below.
            if (ctx.ConnFactory.BulkBatchExecutor is { } bulkExecutor)
            {
                var built = await BulkBatch.BulkBatchPlanBuilder.TryBuildAsync(table, actions, ctx, transformContext);
                if (built is not null)
                {
                    var bulkTotal = await TryExecuteBulkAsync(table, ctx, bulkExecutor, built);
                    if (bulkTotal is not null)
                        return bulkTotal.Value;
                }
            }

            await using var conn = ctx.ConnFactory.GetConnection();
            var outcomes = new List<BatchActionOutcome>();
            DbTransaction? transaction = null;
            try
            {
                await conn.OpenAsync(ct);
                transaction = await conn.BeginTransactionAsync(ct);
                // All per-action executors share the same table/dialect/model,
                // connection + transaction, and captured contexts; bundle them once
                // so the executors take only their per-action data.
                var execContext = new BatchExecutionContext(
                    table, ctx.Transformers, ctx.Model, ctx.ConnFactory.Dialect, conn, transaction,
                    ctx.UserContext, transformContext, ctx.ModuleArguments, ct, ctx.ConnFactory,
                    MutationObserverContext.NewMutationState());
                foreach (var action in actions)
                {
                    var outcome = await ExecuteAction(execContext, action);
                    if (outcome is not null)
                        outcomes.Add(outcome);
                }
                await transaction.CommitAsync(ct);
            }
            catch (BifrostExecutionError)
            {
                if (transaction != null)
                    await transaction.RollbackAsync(ct);
                throw;
            }
            catch (Exception ex)
            {
                if (transaction != null)
                    await transaction.RollbackAsync(ct);
                throw BifrostExecutionError.FromDatabaseException(ex);
            }
            finally
            {
                if (transaction != null)
                    await transaction.DisposeAsync();
            }

            // The approval gate diverted at least one action into a pending change (enqueued on
            // the batch's transaction, now committed). Surface pending-approval AFTER the commit
            // — a batch on a gated table applied nothing and enqueued one pending row per action.
            // Thrown here, outside the try, so it is not caught and wrapped as a database error.
            var pending = outcomes.FirstOrDefault(o => o.PendingApproval is not null);
            if (pending is not null)
                throw ApprovalInterceptMutationHook.PendingApprovalError(pending.PendingApproval!);

            // Observers fire only after commit so audit/state-transition
            // notifications never describe rolled-back work. Failures inside
            // observers are swallowed by MutationObservers/StateTransitionObservers.
            await NotifyObserversAsync(ctx.Services, table, outcomes, ctx.UserContext);

            var totalAffected = 0;
            foreach (var outcome in outcomes) totalAffected += outcome.Affected;
            return totalAffected;
        }

        /// <summary>
        /// Runs the built plan through the provider's set-based executor and fans the result
        /// out to the same post-commit observers the per-row path notifies. Returns null ONLY
        /// for a staging-phase failure (<see cref="BulkBatch.BulkBatchStagingException"/> —
        /// nothing written, the per-row path may safely run the batch instead); any failure
        /// inside the inline transaction propagates as the batch's error.
        /// </summary>
        private static async Task<int?> TryExecuteBulkAsync(
            IDbTable table, MutationPipelineContext ctx,
            BulkBatch.IBulkBatchExecutor executor, BulkBatch.BulkBatchPlanBuilder.BuiltBulkBatch built)
        {
            var ct = ctx.CancellationToken;
            BulkBatch.BulkBatchResult result;
            await using (var conn = ctx.ConnFactory.GetConnection())
            {
                try
                {
                    await conn.OpenAsync(ct);
                    result = await executor.ExecuteAsync(built.Plan, conn, ct);
                }
                catch (BulkBatch.BulkBatchStagingException)
                {
                    return null;
                }
                catch (BifrostExecutionError)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw BifrostExecutionError.FromDatabaseException(ex);
                }
            }

            // Inserts always land exactly one row (a failure aborted the transaction);
            // updates/deletes report per-row via the executor's affected seq set, so a
            // scoped-away row notifies observers with Affected 0, matching the per-row path.
            var outcomes = built.Outcomes
                .Select(o => new BatchActionOutcome(
                    o.MutationType == MutationType.Insert || result.AffectedSeqs.Contains(o.Seq) ? 1 : 0,
                    o.MutationType, o.Data, Transition: null))
                .ToList();
            await NotifyObserversAsync(ctx.Services, table, outcomes, ctx.UserContext);
            return result.TotalAffected;
        }

        private static async ValueTask NotifyObserversAsync(
            IServiceProvider? services,
            IDbTable table,
            IReadOnlyList<BatchActionOutcome> outcomes,
            IDictionary<string, object?> userContext)
        {
            if (services is null || outcomes.Count == 0) return;

            var mutationObservers = services.GetService<MutationObservers>();
            var transitionObservers = services.GetService<StateTransitionObservers>();
            var triggersSuppressed = MutationNotifier.IsWorkflowTriggerSuppressed(userContext);

            foreach (var outcome in outcomes)
            {
                if (mutationObservers is not null && !triggersSuppressed)
                {
                    await mutationObservers.NotifyAsync(new MutationObserverContext
                    {
                        Table = table,
                        MutationType = outcome.MutationType,
                        Data = outcome.Data,
                        Result = outcome.Affected,
                        UserContext = userContext,
                        // Post-commit: no hook phase pairs with this notification, so the
                        // bag is fresh and empty (see MutationNotifier.NotifyMutationAsync).
                        MutationState = MutationObserverContext.NewMutationState(),
                    });
                }
                if (outcome.Transition is not null && transitionObservers is not null)
                {
                    await transitionObservers.NotifyAsync(outcome.Transition, userContext);
                }
            }
        }

        /// <summary>
        /// The invariant-per-batch collaborators every per-action executor needs:
        /// the target table/dialect/model, the shared connection + transaction all
        /// actions commit through, the captured user/transform contexts, the batch-wide
        /// module arguments, and the cancellation token. Bundled so the executors take
        /// only their per-action data dictionary.
        /// </summary>
        private sealed record BatchExecutionContext(
            IDbTable Table,
            IMutationTransformers MutationTransformers,
            IDbModel Model,
            ISqlDialect Dialect,
            DbConnection Conn,
            DbTransaction Transaction,
            IDictionary<string, object?> UserContext,
            MutationTransformContext TransformContext,
            IReadOnlyDictionary<string, object?> ModuleArguments,
            CancellationToken Ct,
            IDbConnFactory ConnFactory,
            IDictionary<string, object?> MutationState);

        private static async Task<BatchActionOutcome?> ExecuteAction(BatchExecutionContext ctx, BatchAction action)
        {
            return action.Action switch
            {
                MutationAction.Insert => await ExecuteInsert(ctx, action.Data),
                MutationAction.Update => await ExecuteUpdate(ctx, action.Data),
                MutationAction.Delete => await ExecuteDelete(ctx, action.Data),
                MutationAction.Upsert => await ExecuteUpsert(ctx, action.Data),
                _ => null,
            };
        }

        /// <summary>
        /// The one hook choreography every batch write runs: before-commit hooks fire
        /// immediately before the write, the write executes, and the after-write
        /// in-transaction hooks (the CDC outbox writer, the history recorder) fire with its
        /// result — all on the batch's shared connection + transaction, the same seam the
        /// single-row pipeline offers, so a hook sees EVERY row of a batch and what it
        /// writes commits or rolls back with the whole batch. A veto (returned errors or a
        /// throw from either phase or the write itself) raises out of the enclosing
        /// transaction, so the whole batch rolls back: a batch is one transaction, and a
        /// row that must not be written cannot be written "except for the other rows
        /// around it". The context — including the state scratchpad that pairs a
        /// before-image with the write it preceded — is scoped per action, never per
        /// batch, so one row's before-image can never be paired with the next row's write.
        /// <paramref name="write"/> returns the generated identity for an insert (so the
        /// event can name the row) or the affected-row count for an update/delete (so a
        /// zero-row no-op records nothing).
        /// </summary>
        private static async Task<(T Result, string? PendingApproval)> RunHookedWriteAsync<T>(
            BatchExecutionContext ctx, MutationType type, IDictionary<string, object?> data,
            Func<Task<T>> write, MutationType? logicalType = null)
        {
            var hookContext = new MutationObserverContext
            {
                Table = ctx.Table,
                MutationType = type,
                Data = data,
                Result = null,
                UserContext = ctx.UserContext,
                Connection = ctx.Conn,
                Transaction = ctx.Transaction,
                Model = ctx.Model,
                Dialect = ctx.Dialect,
                ConnFactory = ctx.ConnFactory,
                MutationState = ctx.MutationState,
            };
            if (logicalType is not null)
                ApprovalInterceptMutationHook.SetLogicalMutationType(hookContext, logicalType.Value);
            await MutationNotifier.RunBeforeCommitHooksAsync(ctx.TransformContext.Services, hookContext);
            // Approval gate: the action was enqueued as a pending change on the batch's shared
            // transaction. Skip its write (and the after-write hooks) so nothing applies; the
            // batch commits the pending row with the rest and surfaces pending-approval.
            if (ApprovalInterceptMutationHook.TryGetDivertMessage(hookContext, out var divertMessage))
                return (default!, divertMessage);
            var result = await write();
            await MutationNotifier.RunInTransactionHooksAsync(ctx.TransformContext.Services, hookContext, result);
            return (result, null);
        }

        private static async Task<BatchActionOutcome?> ExecuteInsert(BatchExecutionContext ctx, Dictionary<string, object?> data)
        {
            if (data.Count == 0) return null;
            var table = ctx.Table;
            var dialect = ctx.Dialect;

            // Mutation transformers (e.g. the authorization policy engine) gate
            // the insert before any SQL is built; non-empty Errors abort it.
            var transformResult = await ctx.MutationTransformers.TransformAsync(table, MutationType.Insert, data, ctx.TransformContext);
            transformResult.ThrowIfDenied();

            // Adopt the (possibly rewritten) data so transformer output — e.g.
            // enum-name → DB-value mapping — reaches the SQL, rekeyed from GraphQL
            // field names to real DB column names. When no transformer applies and
            // names already match, this is effectively a no-op.
            data = ToDbColumnKeys(table, transformResult.Data);

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            // Capture the generated identity (mirroring the single-row insert) so a CDC
            // event can name a row whose key the client did not supply. A successful
            // single-row insert affects exactly one row.
            var insertInto = MutationCommandExecutor.BuildInsertInto(dialect, table, tableRef, data.Keys);
            var returning = dialect.ReturningIdentityClauseFor(table.KeyColumns.Select(k => k.ColumnName).ToList());
            var sql = returning != null
                ? $"{insertInto}{returning};"
                : $"{insertInto};SELECT {dialect.LastInsertedIdentity} ID;";
            var (_, insertPending) = await RunHookedWriteAsync(ctx, MutationType.Insert, data, async () =>
            {
                await using var cmd = ctx.Conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Transaction = ctx.Transaction;
                AddParameters(cmd, data);
                return await cmd.ExecuteScalarAsync(ctx.Ct);
            });
            if (insertPending is not null)
                return new BatchActionOutcome(0, MutationType.Insert, data, null, insertPending);
            return new BatchActionOutcome(1, MutationType.Insert, data, transformResult.StateTransition);
        }

        private static async Task<BatchActionOutcome?> ExecuteUpdate(BatchExecutionContext ctx, Dictionary<string, object?> data)
        {
            if (data.Count == 0) return null;
            var table = ctx.Table;
            var dialect = ctx.Dialect;

            var caseData = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
            // keyData is DB-name space (drives WHERE + current-row load); tolerant of
            // GraphQL field names. standardData keeps GraphQL names for transformers
            // and is normalized to DB names before SQL generation.
            var keyData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in caseData.Where(d => IsPrimaryKeyColumn(table, d.Key)))
                keyData[ToDbColumnName(table, d.Key)] = d.Value;
            var standardData = caseData.Where(d => !IsPrimaryKeyColumn(table, d.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            if (!keyData.Any() || !standardData.Any()) return null;

            var currentRow = await MutationCommandExecutor.LoadCurrentStateMachineRow(ctx.Conn, ctx.Transaction, dialect, table, keyData);
            var updateTransformContext = currentRow is null
                ? ctx.TransformContext
                : new MutationTransformContext
                {
                    Model = ctx.TransformContext.Model,
                    UserContext = ctx.TransformContext.UserContext,
                    CurrentRow = currentRow,
                    Services = ctx.TransformContext.Services,
                };

            // Mutation transformers (e.g. the authorization policy engine) gate
            // the update before any SQL is built; non-empty Errors abort it.
            var transformResult = await ctx.MutationTransformers.TransformAsync(table, MutationType.Update, caseData, updateTransformContext);
            transformResult.ThrowIfDenied();

            // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
            // IS NULL) is ANDed onto the WHERE clause so it narrows — never
            // replaces — the primary-key predicate.
            var additionalFilter = MutationCommandExecutor.RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

            // Adopt the (possibly rewritten) data so transformer output — e.g.
            // enum-name → DB-value mapping — reaches the SQL. The non-key SET split
            // is recomputed against the (unchanged) primary-key set; enum columns are
            // non-key. When no transformer applies, Transform returns the same data
            // reference, so standardData is re-derived identically (no-op).
            var updatedData = ToDbColumnKeys(table, transformResult.Data);
            standardData = updatedData
                .Where(d => !keyData.ContainsKey(d.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var sql = MutationCommandExecutor.BuildUpdateSql(dialect, table, tableRef, standardData.Keys, keyData.Keys, additionalFilter.WhereSuffix);
            var (affected, updatePending) = await RunHookedWriteAsync(ctx, MutationType.Update, updatedData, async () =>
            {
                await using var cmd = ctx.Conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Transaction = ctx.Transaction;
                AddParameters(cmd, updatedData);
                AddExtraParameters(cmd, additionalFilter.Parameters);
                var rows = await cmd.ExecuteNonQueryAsync(ctx.Ct);

                // A zero-row update under a concurrency-token guard is a lost update, not a
                // silent no-op — see DbTableMutateResolver.UpdateObject. Throwing rolls back
                // the whole batch transaction, so a stale row aborts the batch. Raised
                // before the after-write hooks so no event records a rejected write.
                if (transformResult.ConflictOnNoRows && rows == 0)
                    throw new BifrostExecutionError(
                        $"Update of '{table.TableSchema}.{table.DbName}' was rejected: the concurrency token no longer matches — the row was modified or removed since it was read. Reload and retry.")
                    { ErrorCode = "CONFLICT" };

                return rows;
            });
            if (updatePending is not null)
                return new BatchActionOutcome(0, MutationType.Update, updatedData, null, updatePending);
            return new BatchActionOutcome(affected, MutationType.Update, updatedData, transformResult.StateTransition);
        }

        private static async Task<BatchActionOutcome?> ExecuteDelete(BatchExecutionContext ctx, Dictionary<string, object?> data)
        {
            if (data.Count == 0) return null;
            var table = ctx.Table;
            var dialect = ctx.Dialect;

            // Thread the captured module arguments (e.g. _hardDelete) so the
            // soft-delete transformer can read HardDeleteKey and skip the
            // DELETE→UPDATE rewrite, mirroring the single-row resolver.
            var deleteTransformContext = ctx.ModuleArguments.Count == 0
                ? ctx.TransformContext
                : new MutationTransformContext
                {
                    Model = ctx.TransformContext.Model,
                    UserContext = ctx.TransformContext.UserContext,
                    Services = ctx.TransformContext.Services,
                    ModuleArguments = ctx.ModuleArguments,
                };

            var transformResult = await ctx.MutationTransformers.TransformAsync(table, MutationType.Delete, data, deleteTransformContext);
            transformResult.ThrowIfDenied();

            // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
            // IS NULL) is ANDed onto the WHERE clause so it narrows — never
            // replaces — the primary-key predicate.
            var additionalFilter = MutationCommandExecutor.RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

            // Rekey to DB column names so the PK split (via ColumnLookup) and the
            // emitted WHERE/SET share one name space even for sanitized columns.
            var dbData = ToDbColumnKeys(table, transformResult.Data);
            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);

            if (transformResult.MutationType == MutationType.Update)
            {
                // Soft-delete rewrite: primary-key columns scope the WHERE, everything
                // else (the transformer-stamped deleted_at/deleted_by) is written in SET.
                var keyData = dbData.Where(d => IsPrimaryKeyColumn(table, d.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                var setData = dbData.Where(d => !keyData.ContainsKey(d.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                var sql = MutationCommandExecutor.BuildUpdateSql(dialect, table, tableRef, setData.Keys, keyData.Keys, additionalFilter.WhereSuffix);
                var (softAffected, softPending) = await RunHookedWriteAsync(ctx, MutationType.Update, dbData, async () =>
                {
                    await using var cmd = ctx.Conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Transaction = ctx.Transaction;
                    AddParameters(cmd, dbData);
                    AddExtraParameters(cmd, additionalFilter.Parameters);
                    return await cmd.ExecuteNonQueryAsync(ctx.Ct);
                }, MutationType.Delete);
                if (softPending is not null)
                    return new BatchActionOutcome(0, MutationType.Update, transformResult.Data, null, softPending);
                return new BatchActionOutcome(softAffected, MutationType.Update, transformResult.Data, transformResult.StateTransition);
            }

            // Adopt the (possibly rewritten) data so transformer output (e.g.
            // enum-name → DB-value mapping on a predicate column) reaches the
            // WHERE clause and parameters, mirroring the soft-delete branch above.
            var deleteData = dbData;
            var deleteSql = MutationCommandExecutor.BuildDeleteSql(dialect, tableRef, deleteData.Keys, additionalFilter.WhereSuffix);
            var (deleteAffected, deletePending) = await RunHookedWriteAsync(ctx, MutationType.Delete, deleteData, async () =>
            {
                await using var deleteCmd = ctx.Conn.CreateCommand();
                deleteCmd.CommandText = deleteSql;
                deleteCmd.Transaction = ctx.Transaction;
                AddParameters(deleteCmd, deleteData);
                AddExtraParameters(deleteCmd, additionalFilter.Parameters);
                return await deleteCmd.ExecuteNonQueryAsync(ctx.Ct);
            });
            if (deletePending is not null)
                return new BatchActionOutcome(0, MutationType.Delete, deleteData, null, deletePending);
            return new BatchActionOutcome(deleteAffected, MutationType.Delete, deleteData, transformResult.StateTransition);
        }

        private static async Task<BatchActionOutcome?> ExecuteUpsert(BatchExecutionContext ctx, Dictionary<string, object?> data)
        {
            if (data.Count == 0) return null;
            var table = ctx.Table;

            // A true upsert is routed through the real Insert-or-Update decision
            // rather than a native single-statement UpsertSql — the same decision
            // DbTableMutateResolver.UpsertObject records: a single statement
            // (ON CONFLICT / MERGE) cannot express a transformer's AdditionalFilter
            // — tenant/policy row-scope, soft-delete IS NULL — as a guard on its
            // INSERT branch, so a caller could take over a row in another tenant or
            // resurrect a soft-deleted one. It would also run the pipeline as
            // Update with no CurrentRow, skipping state-machine current-state
            // validation and insert-required checks. Probing existence by primary
            // key and dispatching to ExecuteInsert / ExecuteUpdate applies every
            // one of those enforcements exactly as for a plain insert/update.
            //
            // The probe runs inside the batch transaction; the database's
            // primary-key / unique constraint remains the real arbiter under a
            // concurrent writer (a lost insert race fails the INSERT, a lost
            // update race affects 0 rows).
            var caseData = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
            var keyData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in caseData.Where(d => IsPrimaryKeyColumn(table, d.Key)))
                keyData[ToDbColumnName(table, d.Key)] = d.Value;

            if (keyData.Count > 0 && await RowExistsAsync(ctx, keyData))
                return await ExecuteUpdate(ctx, data);

            return await ExecuteInsert(ctx, data);
        }

        // Probes whether a row keyed by the given primary-key values already exists,
        // inside the batch's own transaction, so the upsert path can dispatch to the
        // safe Insert or Update executor (see ExecuteUpsert).
        private static async Task<bool> RowExistsAsync(BatchExecutionContext ctx, Dictionary<string, object?> keyData)
        {
            var tableRef = ctx.Dialect.TableReference(ctx.Table.TableSchema, ctx.Table.DbName);
            var whereClause = MutationCommandExecutor.BuildKeyPredicate(ctx.Dialect, keyData.Keys);
            await using var cmd = ctx.Conn.CreateCommand();
            cmd.CommandText = $"SELECT 1 FROM {tableRef} WHERE {whereClause};";
            cmd.Transaction = ctx.Transaction;
            AddParameters(cmd, keyData);
            var result = await cmd.ExecuteScalarAsync(ctx.Ct);
            return result != null && result != DBNull.Value;
        }
    }
}
