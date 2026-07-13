using System.Data.Common;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
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
            StateTransitionInfo? Transition);

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
                    ctx.UserContext, transformContext, ctx.ModuleArguments, ct);
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

            // Observers fire only after commit so audit/state-transition
            // notifications never describe rolled-back work. Failures inside
            // observers are swallowed by MutationObservers/StateTransitionObservers.
            await NotifyObserversAsync(ctx.Services, table, outcomes, ctx.UserContext);

            var totalAffected = 0;
            foreach (var outcome in outcomes) totalAffected += outcome.Affected;
            return totalAffected;
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
            CancellationToken Ct);

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
        private static async Task<T> RunHookedWriteAsync<T>(
            BatchExecutionContext ctx, MutationType type, IDictionary<string, object?> data,
            Func<Task<T>> write)
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
                MutationState = MutationObserverContext.NewMutationState(),
            };
            await MutationNotifier.RunBeforeCommitHooksAsync(ctx.TransformContext.Services, hookContext);
            var result = await write();
            await MutationNotifier.RunInTransactionHooksAsync(ctx.TransformContext.Services, hookContext, result);
            return result;
        }

        private static async Task<BatchActionOutcome?> ExecuteInsert(BatchExecutionContext ctx, Dictionary<string, object?> data)
        {
            if (data.Count == 0) return null;
            var table = ctx.Table;
            var dialect = ctx.Dialect;

            // Mutation transformers (e.g. the authorization policy engine) gate
            // the insert before any SQL is built; non-empty Errors abort it.
            var transformResult = await ctx.MutationTransformers.TransformAsync(table, MutationType.Insert, data, ctx.TransformContext);
            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

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
            await RunHookedWriteAsync(ctx, MutationType.Insert, data, async () =>
            {
                await using var cmd = ctx.Conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Transaction = ctx.Transaction;
                AddParameters(cmd, data);
                return await cmd.ExecuteScalarAsync(ctx.Ct);
            });
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
            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

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
            var affected = await RunHookedWriteAsync(ctx, MutationType.Update, updatedData, async () =>
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
            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

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
                var softAffected = await RunHookedWriteAsync(ctx, MutationType.Update, dbData, async () =>
                {
                    await using var cmd = ctx.Conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Transaction = ctx.Transaction;
                    AddParameters(cmd, dbData);
                    AddExtraParameters(cmd, additionalFilter.Parameters);
                    return await cmd.ExecuteNonQueryAsync(ctx.Ct);
                });
                return new BatchActionOutcome(softAffected, MutationType.Update, transformResult.Data, transformResult.StateTransition);
            }

            // Adopt the (possibly rewritten) data so transformer output (e.g.
            // enum-name → DB-value mapping on a predicate column) reaches the
            // WHERE clause and parameters, mirroring the soft-delete branch above.
            var deleteData = dbData;
            var deleteSql = MutationCommandExecutor.BuildDeleteSql(dialect, tableRef, deleteData.Keys, additionalFilter.WhereSuffix);
            var deleteAffected = await RunHookedWriteAsync(ctx, MutationType.Delete, deleteData, async () =>
            {
                await using var deleteCmd = ctx.Conn.CreateCommand();
                deleteCmd.CommandText = deleteSql;
                deleteCmd.Transaction = ctx.Transaction;
                AddParameters(deleteCmd, deleteData);
                AddExtraParameters(deleteCmd, additionalFilter.Parameters);
                return await deleteCmd.ExecuteNonQueryAsync(ctx.Ct);
            });
            return new BatchActionOutcome(deleteAffected, MutationType.Delete, deleteData, transformResult.StateTransition);
        }

        private static async Task<BatchActionOutcome?> ExecuteUpsert(BatchExecutionContext ctx, Dictionary<string, object?> data)
        {
            if (data.Count == 0) return null;
            var table = ctx.Table;
            var dialect = ctx.Dialect;

            var caseData = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
            // Build the statement in DB-column-name space so sanitized GraphQL
            // field names resolve to real columns; transform still runs on the
            // GraphQL-keyed caseData below, and the bound data is rekeyed to match.
            var dbColumns = caseData.Keys.Select(k => ToDbColumnName(table, k)).ToList();
            var keyColumns = dbColumns.Where(k => table.ColumnLookup[k].IsPrimaryKey).ToList();
            var updateColumns = dbColumns.Where(k => !table.ColumnLookup[k].IsPrimaryKey).ToList();
            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var upsertSql = dialect.UpsertSql(tableRef, keyColumns, dbColumns, updateColumns);

            if (upsertSql != null)
            {
                // An upsert that resolves to a single statement is gated as an
                // update: it targets an existing or new row keyed by primary key.
                var transformResult = await ctx.MutationTransformers.TransformAsync(table, MutationType.Update, caseData, ctx.TransformContext);
                if (transformResult.Errors.Length > 0)
                    throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

                // The single-statement upsert (ON CONFLICT / MERGE) neither renders the
                // transformer's AdditionalFilter nor can express a "fail if the token
                // moved" WHERE — it always writes. Silently dropping a concurrency-token
                // guard here would defeat lost-update protection, so refuse the path
                // rather than bypass the guard. Plain update/upsert (multi-statement)
                // enforces it correctly.
                if (transformResult.ConflictOnNoRows)
                    throw new BifrostExecutionError(
                        $"Optimistic-concurrency table '{table.TableSchema}.{table.DbName}' cannot be written through the single-statement batch upsert path (the token guard cannot be enforced there). Use a plain update.")
                    { ErrorCode = "CONFLICT" };

                // Adopt the (possibly rewritten) data so transformer output — e.g.
                // enum-name → DB-value mapping — reaches the SQL. The key/non-key
                // split (and therefore upsertSql) is unaffected because transformers
                // rewrite values, not primary-key membership. When no transformer
                // applies, Transform returns the same data reference (no-op).
                var upsertData = ToDbColumnKeys(table, transformResult.Data);
                // Upsert is keyed by primary key, so upsertData carries the key the event
                // needs even when the statement inserts a new row.
                var affected = await RunHookedWriteAsync(ctx, MutationType.Update, upsertData, async () =>
                {
                    await using var cmd = ctx.Conn.CreateCommand();
                    cmd.CommandText = upsertSql;
                    cmd.Transaction = ctx.Transaction;
                    AddParameters(cmd, upsertData);
                    return await cmd.ExecuteNonQueryAsync(ctx.Ct);
                });
                return new BatchActionOutcome(affected, MutationType.Update, upsertData, transformResult.StateTransition);
            }

            if (keyColumns.Count > 0)
                return await ExecuteUpdate(ctx, data);

            return await ExecuteInsert(ctx, data);
        }
    }
}
