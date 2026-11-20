using System.Data.Common;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Approval;
using BifrostQL.Core.QueryModel;
using BifrostQL.Model;
using static BifrostQL.Core.Resolvers.DbParameterBinder;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// The dependencies a single-row mutation needs once GraphQL context has been
    /// stripped away. Built by <see cref="DbTableMutateResolver"/> from the field
    /// context and by <see cref="MutationIntentExecutor"/> from an endpoint's cached
    /// inputs, so both entry points execute the identical pipeline.
    /// </summary>
    internal sealed class MutationPipelineContext
    {
        public required IDbModel Model { get; init; }
        public required IDbConnFactory ConnFactory { get; init; }
        public required IMutationTransformers Transformers { get; init; }
        public required IDictionary<string, object?> UserContext { get; init; }
        public IServiceProvider? Services { get; init; }

        /// <summary>
        /// Module argument values (e.g. <c>_hardDelete</c>) keyed by context key;
        /// consumed by the delete path's transformers. Empty when the entry point
        /// carries none.
        /// </summary>
        public IReadOnlyDictionary<string, object?> ModuleArguments { get; init; } = ModuleApiRegistry.EmptyArguments;

        /// <summary>
        /// The mutation operation's shared transaction, when this write is one step of an
        /// operation (<see cref="OperationWriteScope"/>). Null for every generated write, which
        /// opens and commits its own transaction.
        /// </summary>
        public OperationWriteScope? WriteScope { get; init; }

        /// <summary>
        /// True when this write carries a caller operation id that its <see cref="WriteScope"/>
        /// records; copied to every transformer context
        /// (<see cref="MutationTransformContext.RecordsOperationId"/>).
        /// </summary>
        public bool RecordsOperationId { get; init; }

        /// <summary>
        /// The server operation making this write, null for a generated write; copied to every
        /// transformer context (<see cref="MutationTransformContext.Operation"/>).
        /// </summary>
        public Operations.IBifrostOperation? Operation { get; init; }

        /// <summary>
        /// Entries an operation hands its write (<c>BifrostOperationContext.InsertAsync</c>'s
        /// <c>state</c>): copied into the write's <see cref="MutationTransformContext.MutationState"/>
        /// before the chain runs, so the transformers and both hook phases start from them.
        /// </summary>
        public IReadOnlyDictionary<string, object?>? InitialMutationState { get; init; }

        /// <summary>Allows the deferred undo seam to target a soft-deleted row while preserving every other mutation transformer.</summary>
        public bool RestoreSoftDeleted { get; init; }

        /// <summary>Marks re-insertion of a hard-deleted deferred row.</summary>
        public bool RestoreHardDeleted { get; init; }

        public CancellationToken CancellationToken { get; init; }
    }

    /// <summary>
    /// The shared single-row mutation execution seam: transformer chain (policy,
    /// state machine, enum mapping, validation, soft delete, tenant isolation,
    /// audit, optimistic concurrency), before-commit hooks, parameterized SQL, and
    /// post-commit notifications. Extracted from <see cref="DbTableMutateResolver"/>
    /// so the GraphQL resolver and the protocol-adapter mutation-intent path run
    /// ONE pipeline — transformer application lives inside these methods, so no
    /// caller has an API surface that reaches SQL without it.
    /// </summary>
    internal static class TableMutationPipeline
    {
        /// <summary>
        /// The per-mutation hook context shared by BOTH in-transaction hook phases (see
        /// <see cref="MutationNotifier.RunBeforeCommitHooksAsync"/>), carrying a fresh
        /// <see cref="MutationObserverContext.NewMutationState"/> bag so one mutation's
        /// before-image can never be paired with another mutation's write. Its transaction
        /// bag is the operation scope's when the write runs inside one, so every write of an
        /// operation shares one deferred change set.
        /// </summary>
        private static MutationObserverContext HookContext(
            IDbTable table, MutationType mutationType, IDictionary<string, object?> data,
            MutationPipelineContext ctx, DbConnection conn, DbTransaction? transaction, ISqlDialect dialect,
            IDictionary<string, object?> mutationState)
            => new()
            {
                Table = table,
                MutationType = mutationType,
                Data = data,
                Result = null,
                UserContext = ctx.UserContext,
                Connection = conn,
                Transaction = transaction,
                Model = ctx.Model,
                Dialect = dialect,
                ConnFactory = ctx.ConnFactory,
                CancellationToken = ctx.CancellationToken,
                MutationState = mutationState,
                TransactionState = ctx.WriteScope?.TransactionState ?? MutationObserverContext.NewMutationState(),
            };

        /// <summary>
        /// One write's <see cref="MutationObserverContext.MutationState"/>: the bag its transformer
        /// chain and both hook phases share, so a transformer that loaded the row can hand it to
        /// the after-write hook. Fresh per write (never the transaction's), started from the
        /// operation's <see cref="MutationPipelineContext.InitialMutationState"/> when it gave one.
        /// </summary>
        private static IDictionary<string, object?> NewWriteState(MutationPipelineContext ctx)
        {
            var state = MutationObserverContext.NewMutationState();
            if (ctx.InitialMutationState is { } seed)
                foreach (var (key, value) in seed)
                    state[key] = value;
            return state;
        }

        /// <summary>
        /// The transformer context every chain run of this pipeline receives: the request's
        /// identity, services and restore flags, plus the write's own connection and
        /// transaction — every chain run here happens inside it.
        /// </summary>
        private static MutationTransformContext TransformContext(
            MutationPipelineContext ctx, DbConnection conn, DbTransaction transaction, ISqlDialect dialect,
            IDictionary<string, object?> mutationState,
            IReadOnlyDictionary<string, object?>? currentRow = null,
            IReadOnlyDictionary<string, object?>? moduleArguments = null)
            => new()
            {
                MutationState = mutationState,
                Model = ctx.Model,
                UserContext = ctx.UserContext,
                CurrentRow = currentRow,
                Services = ctx.Services,
                ModuleArguments = moduleArguments ?? ModuleApiRegistry.EmptyArguments,
                RestoreSoftDeleted = ctx.RestoreSoftDeleted,
                RestoreHardDeleted = ctx.RestoreHardDeleted,
                RecordsOperationId = ctx.RecordsOperationId,
                Operation = ctx.Operation,
                Connection = conn,
                Transaction = transaction,
                Dialect = dialect,
                CancellationToken = ctx.CancellationToken,
            };

        /// <summary>
        /// History targets take no client writes on ANY pipeline entry point —
        /// GraphQL mutation, batch, or adapter mutation intent. Their only writer is
        /// the change-history hook, which inserts trail rows via direct SQL inside
        /// the tracked write's transaction and never routes through this pipeline,
        /// so this guard cannot block a legitimate trail write. Without it an
        /// adapter could forge or edit trail rows through IMutationIntentExecutor.
        /// Shared with the batch resolver and the TreeSync executor so every write
        /// seam raises the same typed access-denied error. The same holds for the
        /// other module-owned stores: the deferred-effects change sets, and the
        /// approval queue, which only the approval gate and its decisions write.
        /// </summary>
        internal static void GuardNotHistoryTarget(IDbTable table, IDbModel model)
        {
            if (Schema.HistorySurface.IsHistoryTarget(model, table))
                throw new BifrostExecutionError(
                    $"Table '{table.TableSchema}.{table.DbName}' is a change-history table and is not writable. " +
                    "Trail rows are written only by the change-history writer, in the tracked write's transaction.")
                { ErrorCode = BifrostExecutionError.AccessDeniedCode };

            if (IsDeferredStoreTarget(table, model))
                throw new BifrostExecutionError(
                    $"Table '{table.TableSchema}.{table.DbName}' is an internal deferred-effects table and is not writable. " +
                    "Change sets and reverse deltas are written only by the deferred-effects hook, in the tracked write's transaction.")
                { ErrorCode = BifrostExecutionError.AccessDeniedCode };

            if (Modules.Approval.PendingChangeStore.IsApprovalQueue(model, table))
                throw new BifrostExecutionError(Modules.Approval.PendingChangeStore.DirectWriteRefusal)
                { ErrorCode = BifrostExecutionError.AccessDeniedCode };
        }

        private static bool IsDeferredStoreTarget(IDbTable table, IDbModel model)
            => IsSameTable(table, ModelTableReference.Find(model, MetadataKeys.Deferred.ChangeSet.Table))
               || IsSameTable(table, ModelTableReference.Find(model, MetadataKeys.Deferred.ChangeSetDelta.Table));

        private static bool IsSameTable(IDbTable left, IDbTable? right)
            => right is not null
               && string.Equals(left.TableSchema, right.TableSchema, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.DbName, right.DbName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether an update's arguments have the shape the pipeline writes: a key, all of it,
        /// and a non-key column to SET. Any other shape is answered by its shape (a no-op 0,
        /// <see cref="MutationArgumentBinder.PartialPrimaryKeyCode"/>,
        /// <see cref="MutationArgumentBinder.NoUpdateColumnsCode"/>), but only after
        /// <see cref="AnswerUnwritableUpdateShapeAsync"/> has run the chain on it.
        /// </summary>
        internal static bool IsWritableUpdateShape(
            IDbTable table, IReadOnlyDictionary<string, object?> keyData,
            IReadOnlyCollection<KeyValuePair<string, object?>> standardData)
        {
            if (keyData.Count == 0 || standardData.Count == 0)
                return false;
            var keyColumns = table.KeyColumns.ToList();
            return keyColumns.Count <= 1
                || keyColumns.All(c => keyData.ContainsKey(c.ColumnName));
        }

        /// <summary>
        /// Answers an update <see cref="IsWritableUpdateShape"/> refuses. Authorization lives in
        /// the transformer chain, so the chain runs first on the data the request gave and its
        /// refusal is the answer: a caller the chain denies never learns the shape check's
        /// answer, and a denied write never reads as a no-op success. For a write the chain
        /// passes, the shape answers as before: it returns (the caller answers a no-op 0) when
        /// no key column was given, else throws the partial-key or no-SET-column error. Every update path calls this — the
        /// single-row pipeline, the batch and the bulk staging — before its own shape checks.
        /// </summary>
        internal static async Task AnswerUnwritableUpdateShapeAsync(
            IMutationTransformers transformers, IDbTable table, Dictionary<string, object?> data,
            IReadOnlyDictionary<string, object?> keyData, IReadOnlyCollection<KeyValuePair<string, object?>> standardData,
            MutationTransformContext transformContext)
        {
            (await transformers.TransformAsync(table, MutationType.Update, data, transformContext)).ThrowIfDenied();
            if (keyData.Count == 0)
                return;
            MutationArgumentBinder.RequireCompleteKey(table, keyData.Keys, "Update");
            MutationArgumentBinder.RequireSetColumns(standardData);
        }

        /// <summary>
        /// Runs the transformer chain on a write the caller answers as a no-op without writing
        /// (an empty upsert, a key-only upsert of an existing row), in the write's transaction,
        /// and throws its refusal. Authorization lives in the chain, so a no-op answer given
        /// before it would let a refused caller read a denied write as one that ran.
        /// </summary>
        internal static Task ThrowIfChainRefusesNoOpAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationPipelineContext ctx)
            => RunInTransactionAsync(ctx, async (conn, transaction) =>
                (await ctx.Transformers.TransformAsync(
                    table, mutationType, data, TransformContext(ctx, conn, transaction, ctx.ConnFactory.Dialect, NewWriteState(ctx)))).ThrowIfDenied());

        /// <summary>
        /// Runs one write's transformer chain, hooks and SQL in a transaction: its own, or the
        /// operation's shared <see cref="MutationPipelineContext.WriteScope"/> when it has one.
        /// </summary>
        private static Task RunInTransactionAsync(
            MutationPipelineContext ctx, Func<DbConnection, DbTransaction, Task> work)
            => ctx.WriteScope is { } scope
                ? scope.JoinAsync(work)
                : MutationCommandExecutor.RunInTransactionAsync(ctx.ConnFactory, work, ctx.CancellationToken);

        /// <summary>
        /// Post-commit notifications run after the write's commit: at once for a standalone
        /// write, after the operation's commit for a write inside a <see cref="OperationWriteScope"/>.
        /// </summary>
        private static async ValueTask AfterCommitAsync(MutationPipelineContext ctx, Func<ValueTask> notify)
        {
            if (ctx.WriteScope is { } scope)
                scope.AfterCommit(async () => await notify());
            else
                await notify();
        }

        /// <summary>
        /// Executes an insert and returns ONLY the generated identity. Callers that report a
        /// row count (protocol adapters) use <see cref="InsertWithAffectedRowsAsync"/>: a trigger
        /// can drop the row, so returning without a throw is not "wrote one row".
        /// </summary>
        public static async Task<object?> InsertAsync(
            IDbTable table, Dictionary<string, object?> data, MutationPipelineContext ctx) =>
            (await InsertWithAffectedRowsAsync(table, data, ctx)).Value;

        /// <summary>
        /// Executes an insert and returns the generated identity plus the REAL number of rows the
        /// INSERT changed (0 or 1). Events and observers fire only when a row was written.
        /// </summary>
        public static async Task<(object? Value, int AffectedRows)> InsertWithAffectedRowsAsync(
            IDbTable table, Dictionary<string, object?> data, MutationPipelineContext ctx)
        {
            GuardNotHistoryTarget(table, ctx.Model);
            var dialect = ctx.ConnFactory.Dialect;

            // The transformer chain, before-commit hooks and the insert (with its identity
            // SELECT) run in one transaction: the chain sees the write's connection (a guard
            // can read and lock state that commits with the write), and a veto or a failed
            // write rolls back as a unit.
            object? result = null;
            var affectedRows = 0;
            string? pendingApproval = null;
            await RunInTransactionAsync(ctx, async (conn, transaction) =>
            {
                // Mutation transformers (e.g. the authorization policy engine) gate
                // the insert before any SQL is built; non-empty Errors abort it.
                var mutationState = NewWriteState(ctx);
                var transformContext = TransformContext(ctx, conn, transaction, dialect, mutationState);
                var transformResult = await ctx.Transformers.TransformAsync(table, MutationType.Insert, data, transformContext);
                transformResult.ThrowIfDenied();

                // Adopt the (possibly rewritten) data so transformer output — e.g.
                // enum-name → DB-value mapping — actually reaches the SQL. It is already
                // keyed by database column name: the chain rekeys once on the way in.
                data = transformResult.Data;

                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
                var insertInto = MutationCommandExecutor.BuildInsertInto(dialect, table, tableRef, data.Keys);
                var returning = dialect.ReturningIdentityClauseFor(table.KeyColumns.Select(k => k.ColumnName).ToList());
                var sql = returning != null
                    ? $"{insertInto}{returning};"
                    : $"{insertInto};SELECT {dialect.LastInsertedIdentity} ID;";

                var hookContext = HookContext(table, MutationType.Insert, data, ctx, conn, transaction, dialect, mutationState);
                await MutationNotifier.RunBeforeCommitHooksAsync(ctx.Services, hookContext);
                // The approval gate DIVERTS a gated write: it enqueued a pending_changes row
                // on this transaction, so skip the target write and let the transaction commit
                // (persisting the pending row), then surface pending-approval below.
                if (ApprovalInterceptMutationHook.TryGetDivertMessage(hookContext, out var divertMessage))
                {
                    pendingApproval = divertMessage;
                    return;
                }
                var (identity, affected) = await MutationCommandExecutor.ExecuteInsert(conn, transaction, sql, data, ctx.CancellationToken);
                result = HandleDecimals(identity);
                affectedRows = affected;
                // After-write, still in-transaction: the outbox writer runs here so the
                // event can name the generated identity (result) returned by the insert.
                // The hooks skip AffectedRows == 0, so a trigger-dropped row emits no event.
                await MutationNotifier.RunInTransactionHooksAsync(ctx.Services, hookContext, result, affectedRows);
            });
            // Thrown AFTER the commit so the enqueued pending row survives; never
            // reported as success-as-applied.
            if (pendingApproval is not null)
                throw ApprovalInterceptMutationHook.PendingApprovalError(pendingApproval);
            if (affectedRows > 0)
                await AfterCommitAsync(ctx, () => MutationNotifier.NotifyMutationAsync(
                    ctx.Services, table, MutationType.Insert, data, result, ctx.UserContext, affectedRows));
            return (result, affectedRows);
        }

        /// <summary>
        /// Inserts many rows as one write, in one transaction (the operation's, when the context
        /// carries a <see cref="MutationPipelineContext.WriteScope"/>): each row runs the
        /// transformer chain and the before-commit hooks with its own state bag, and a refusal of
        /// any row refuses the whole call before anything is written. The rows are then written by
        /// one multi-row INSERT per distinct column set (one, when every row writes the same
        /// columns), the in-transaction hooks run once for the set (a set hook) or once per row (a
        /// per-row hook), and the keys come back in row order. Notifications fire per row after
        /// commit. A table under an approval gate cannot take a set write: the gate diverts one row
        /// at a time, so the call is refused rather than half-diverted.
        /// </summary>
        public static async Task<IReadOnlyList<object?>> InsertManyAsync(
            IDbTable table, IReadOnlyList<Dictionary<string, object?>> rows, MutationPipelineContext ctx)
        {
            GuardNotHistoryTarget(table, ctx.Model);
            if (rows.Count == 0)
                return Array.Empty<object?>();
            var dialect = ctx.ConnFactory.Dialect;

            var written = new Dictionary<string, object?>[rows.Count];
            var states = new IDictionary<string, object?>[rows.Count];
            object?[] keys = Array.Empty<object?>();
            await RunInTransactionAsync(ctx, async (conn, transaction) =>
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    var mutationState = NewWriteState(ctx);
                    var transformResult = await ctx.Transformers.TransformAsync(
                        table, MutationType.Insert, rows[i], TransformContext(ctx, conn, transaction, dialect, mutationState));
                    transformResult.ThrowIfDenied();
                    var hookContext = HookContext(table, MutationType.Insert, transformResult.Data, ctx, conn, transaction, dialect, mutationState);
                    await MutationNotifier.RunBeforeCommitHooksAsync(ctx.Services, hookContext);
                    if (ApprovalInterceptMutationHook.TryGetDivertMessage(hookContext, out _))
                        throw new BifrostExecutionError(
                            $"'{table.GraphQlName}' is under an approval gate, which holds rows one at a time; insert them one by one.");
                    written[i] = transformResult.Data;
                    states[i] = mutationState;
                }

                keys = new object?[rows.Count];
                foreach (var group in Enumerable.Range(0, rows.Count).GroupBy(i => ColumnSetKey(written[i])))
                {
                    var indices = group.ToList();
                    var groupKeys = await MutationCommandExecutor.ExecuteInsertMany(
                        conn, transaction, dialect, table, written[indices[0]].Keys.ToList(),
                        indices.Select(i => (IReadOnlyDictionary<string, object?>)written[i]).ToList(),
                        ctx.CancellationToken);
                    for (var g = 0; g < indices.Count; g++)
                        keys[indices[g]] = groupKeys[g];
                }

                var set = new MutationSetContext
                {
                    Table = table,
                    MutationType = MutationType.Insert,
                    Rows = Enumerable.Range(0, rows.Count).Select(i => new MutationSetRow(written[i], keys[i], states[i])).ToList(),
                    UserContext = ctx.UserContext,
                    Connection = conn,
                    Transaction = transaction,
                    Model = ctx.Model,
                    Dialect = dialect,
                    ConnFactory = ctx.ConnFactory,
                    TransactionState = ctx.WriteScope?.TransactionState ?? MutationObserverContext.NewMutationState(),
                    CancellationToken = ctx.CancellationToken,
                };
                await MutationNotifier.RunInTransactionSetHooksAsync(ctx.Services, set, row =>
                    HookContext(table, MutationType.Insert, row.Data, ctx, conn, transaction, dialect, row.MutationState) with
                    {
                        Result = row.Key,
                        AffectedRows = 1,
                        TransactionState = set.TransactionState,
                    });
            });

            for (var i = 0; i < rows.Count; i++)
            {
                var (data, key) = (written[i], keys[i]);
                await AfterCommitAsync(ctx, () => MutationNotifier.NotifyMutationAsync(
                    ctx.Services, table, MutationType.Insert, data, key, ctx.UserContext, 1));
            }
            return keys;
        }

        /// <summary>The written column set of one row, the grouping key of a multi-row insert.</summary>
        private static string ColumnSetKey(Dictionary<string, object?> row)
            => string.Join("\u0000", row.Keys.Order(StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Executes an update and returns ONLY the mutation field's scalar value.
        ///
        /// <para><b>That value is not an affected-row count and must never be read
        /// as one.</b> The GraphQL update field is typed <c>Int</c> and cannot carry
        /// a composite key, so this return is a back-compat compromise: a
        /// single-key table yields the KEY VALUE, a composite-key table yields the
        /// affected row count. A caller that tests it for <c>== 0</c> is therefore
        /// inert for every nonzero single-column key and misfires on key value 0.
        /// Callers that need to know whether a row actually changed — TOCTOU
        /// guards, blob-compensation paths — must call
        /// <see cref="UpdateWithAffectedRowsAsync"/> instead.</para>
        /// </summary>
        public static async Task<object?> UpdateAsync(
            IDbTable table,
            (Dictionary<string, object?> data, Dictionary<string, object?> keyData, Dictionary<string, object?> standardData) propertyInfo,
            MutationPipelineContext ctx) =>
            (await UpdateWithAffectedRowsAsync(table, propertyInfo, ctx)).Value;

        /// <summary>
        /// Executes an update, returning both the mutation field's scalar value
        /// (see <see cref="UpdateAsync"/>) and the REAL number of rows the UPDATE
        /// statement affected — the only trustworthy "did anything change?" signal.
        /// Zero means the write was scoped away (tenant/policy/soft-delete filter
        /// narrowed it to no rows) or the row vanished between read and write.
        /// </summary>
        public static async Task<(object? Value, int AffectedRows)> UpdateWithAffectedRowsAsync(
            IDbTable table,
            (Dictionary<string, object?> data, Dictionary<string, object?> keyData, Dictionary<string, object?> standardData) propertyInfo,
            MutationPipelineContext ctx)
        {
            GuardNotHistoryTarget(table, ctx.Model);

            var dialect = ctx.ConnFactory.Dialect;

            // The state-machine load, mutation transformers, before-commit hooks and
            // the update run inside one transaction so the read the transformer gates
            // on and the write it produces commit atomically or roll back together.
            Dictionary<string, object?> updatedData = null!;
            int result = 0;
            string? pendingApproval = null;
            StateTransitionInfo? stateTransition = null;
            var writable = true;
            await RunInTransactionAsync(ctx, async (conn, transaction) =>
            {
                // No key, a partial key (it would widen the WHERE from one row to every row
                // sharing the supplied key columns, and the state-machine load reads by the
                // same predicate) or nothing to SET: the chain answers first, then the shape.
                if (!IsWritableUpdateShape(table, propertyInfo.keyData, propertyInfo.standardData))
                {
                    await AnswerUnwritableUpdateShapeAsync(
                        ctx.Transformers, table, propertyInfo.data, propertyInfo.keyData, propertyInfo.standardData,
                        TransformContext(ctx, conn, transaction, dialect, NewWriteState(ctx)));
                    writable = false;
                    return;
                }

                // Mutation transformers (e.g. the authorization policy engine) gate
                // the update before any SQL is built; non-empty Errors abort it.
                // The state load runs under this same context minus CurrentRow — that is
                // what it is about to produce — so the chain can report the caller's row
                // scope and the load can narrow itself to rows this update could reach.
                var mutationState = NewWriteState(ctx);
                var scopeContext = TransformContext(ctx, conn, transaction, dialect, mutationState);
                var currentRow = await MutationCommandExecutor.LoadCurrentStateMachineRow(
                    conn,
                    transaction,
                    dialect,
                    table,
                    propertyInfo.keyData,
                    ctx.Transformers,
                    scopeContext,
                    ctx.CancellationToken);
                var transformContext = TransformContext(ctx, conn, transaction, dialect, mutationState, currentRow);
                var transformResult = await ctx.Transformers.TransformAsync(table, MutationType.Update, propertyInfo.data, transformContext);
                transformResult.ThrowIfDenied();

                // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
                // IS NULL) is ANDed onto the WHERE clause so it narrows — never
                // replaces — the primary-key predicate.
                var additionalFilter = MutationCommandExecutor.RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

                // Adopt the (possibly rewritten) data so transformer output — e.g.
                // enum-name → DB-value mapping — reaches the SQL. Both it and keyData
                // (see MutationArgumentBinder) are database-named, so the non-key split
                // and the WHERE share one name space; enum columns are non-key.
                updatedData = transformResult.Data;
                var standardData = updatedData
                    .Where(d => !propertyInfo.keyData.ContainsKey(d.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
                var sql = MutationCommandExecutor.BuildUpdateSql(dialect, table, tableRef, standardData.Keys, propertyInfo.keyData, additionalFilter.WhereSuffix);
                var hookData = new Dictionary<string, object?>(updatedData, StringComparer.OrdinalIgnoreCase);
                foreach (var key in propertyInfo.keyData)
                    hookData[key.Key] = key.Value;
                var hookContext = HookContext(table, MutationType.Update, hookData, ctx, conn, transaction, dialect, mutationState);
                await MutationNotifier.RunBeforeCommitHooksAsync(ctx.Services, hookContext);
                // Approval gate: the update was enqueued as a pending change — skip the write,
                // commit the pending row, surface pending-approval after the transaction.
                if (ApprovalInterceptMutationHook.TryGetDivertMessage(hookContext, out var divertMessage))
                {
                    pendingApproval = divertMessage;
                    return;
                }
                result = await MutationCommandExecutor.ExecuteNonQuery(conn, transaction, sql, updatedData, additionalFilter.Parameters, ctx.CancellationToken);

                // A zero-row update under a concurrency-token guard is a lost update:
                // the token predicate matched no row, so the stored version moved since
                // the client read it. Raise a CONFLICT (rolls back this transaction)
                // rather than returning a silent no-op the way an out-of-scope
                // tenant/policy update does. Gated by the transformer's flag so those
                // legitimately-zero-row cases stay silent.
                MutationCommandExecutor.EnsureAffectedRows(
                    result, transformResult.ConflictOnNoRows, inferredTarget: false,
                    $"{table.TableSchema}.{table.DbName}");

                // After the write and the conflict check: emit the event only if a row
                // actually changed (the hooks skip AffectedRows == 0), so an out-of-scope
                // tenant/policy no-op does not fabricate an event.
                await MutationNotifier.RunInTransactionHooksAsync(ctx.Services, hookContext, result, result);

                // Same rule as the in-transaction hooks above: a state transition is only
                // real if a row actually changed state. A tenant/policy-scoped-away update
                // affects zero rows, so publishing its transition would hand the observer
                // chain — webhooks, CDC, workflow triggers — an event describing a row the
                // caller never touched, and whose from/to states it is not allowed to see.
                if (result > 0)
                    stateTransition = transformResult.StateTransition;
            });
            if (!writable)
                return (0, 0);
            if (pendingApproval is not null)
                throw ApprovalInterceptMutationHook.PendingApprovalError(pendingApproval);
            // Observers fire only for a write that changed a row: a tenant/policy-scoped-away
            // update affects none, and an on-mutation workflow must not start for it.
            if (result > 0)
                await AfterCommitAsync(ctx, async () =>
                {
                    await MutationNotifier.NotifyMutationAsync(ctx.Services, table, MutationType.Update, updatedData, result, ctx.UserContext, affectedRows: result);
                    await MutationNotifier.NotifyStateTransitionAsync(ctx.Services, stateTransition, ctx.UserContext);
                });
            // The update mutation field is typed `Int`, so its scalar return cannot
            // carry a composite key. For a single-key table we keep returning the key
            // value (identifies the affected row, back-compat). For a composite key
            // `keyData.Values.First()` would silently surface only the FIRST key
            // component — misleading — so instead return the affected row count
            // (0 or 1), consistent with the delete mutation.
            return (propertyInfo.keyData.Count == 1
                ? propertyInfo.keyData.Values.First()
                : result, result);
        }

        public static async Task<object?> DeleteAsync(
            IDbTable table, Dictionary<string, object?> data, MutationPipelineContext ctx)
        {
            GuardNotHistoryTarget(table, ctx.Model);

            var dialect = ctx.ConnFactory.Dialect;

            // Snapshot the columns the caller actually supplied (in DB-name space),
            // captured BEFORE the pipeline runs. These are the intended WHERE-predicate
            // columns (primary key + any extra predicate). Columns a transformer stamps
            // afterwards (audit updated_at/deleted_at, soft-delete deleted_at/deleted_by)
            // are NOT in this set and must never contaminate the delete predicate.
            var clientColumns = new HashSet<string>(
                data.Keys.Select(k => ToDbColumnName(table, k)),
                StringComparer.OrdinalIgnoreCase);

            // The transformer chain, before-commit hooks and the write run in one
            // transaction, so the chain sees the write's connection and a veto rolls the
            // whole delete back.
            DeleteOutcome outcome = default;
            await RunInTransactionAsync(ctx, async (conn, transaction) =>
            {
                var mutationState = NewWriteState(ctx);
                var transformContext = TransformContext(ctx, conn, transaction, dialect, mutationState, moduleArguments: ctx.ModuleArguments);
                var transformResult = await ctx.Transformers.TransformAsync(table, MutationType.Delete, data, transformContext);

                transformResult.ThrowIfDenied();

                // A delete naming no column deletes nothing, but only after the chain has run
                // on it: a caller the chain refuses gets the refusal, never a 0 that reads as
                // a delete that ran (see AnswerUnwritableUpdateShapeAsync).
                if (data.Count == 0)
                    return;

                // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
                // IS NULL) is ANDed onto the WHERE clause so it narrows — never
                // replaces — the primary-key predicate.
                var additionalFilter = MutationCommandExecutor.RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

                // Database-named on the way out of the chain, so the PK split (via
                // ColumnLookup) and the emitted WHERE/SET share one name space even when a
                // GraphQL field name differs from its column.
                var dbData = transformResult.Data;

                // A transformer may rewrite a delete into a soft-delete UPDATE (deleted_at
                // stamped); otherwise it stays a hard DELETE. Both scope their rows by the
                // same client-supplied predicate columns (see SelectPredicateColumns).
                outcome = transformResult.MutationType == MutationType.Update
                    ? await ExecuteSoftDeleteAsync(table, dialect, ctx, conn, transaction, dbData, clientColumns, additionalFilter, mutationState)
                    : await ExecuteHardDeleteAsync(table, dialect, ctx, conn, transaction, dbData, clientColumns, additionalFilter, mutationState);
            });
            // Thrown AFTER the commit so the enqueued pending row survives.
            if (outcome.PendingApproval is not null)
                throw ApprovalInterceptMutationHook.PendingApprovalError(outcome.PendingApproval);
            if (outcome.Affected > 0)
                await AfterCommitAsync(ctx, () => MutationNotifier.NotifyMutationAsync(
                    ctx.Services, table, outcome.NotifyType, outcome.Data, outcome.Affected, ctx.UserContext, affectedRows: outcome.Affected));
            return outcome.Affected;
        }

        /// <summary>
        /// What one in-transaction delete step produced: the affected-row count, the
        /// approval divert message (when the gate enqueued instead of writing), and the
        /// verb + data the post-commit notification describes (a soft delete notifies as
        /// the UPDATE it executed).
        /// </summary>
        private readonly record struct DeleteOutcome(
            int Affected, string? PendingApproval, MutationType NotifyType, Dictionary<string, object?> Data);

        /// <summary>
        /// The columns that scope a delete's WHERE clause: the client-supplied
        /// predicate columns (primary key plus any extra predicate the client sent),
        /// NOT transformer-stamped columns (audit/soft-delete). Including a stamped
        /// column would AND a never-matching term into the WHERE and silently affect
        /// zero rows. Values come from the (possibly rewritten) transformed data so an
        /// enum-name → DB-value mapping on a predicate column still reaches the WHERE.
        /// Shared with the batch pipeline (<see cref="BatchMutationPipeline"/>) so both
        /// delete paths enforce the same WHERE/SET contract — including the completeness
        /// of the primary key, which is checked here so every delete seam (single-row
        /// hard, single-row soft, batch) gets it from one place: a predicate carrying
        /// SOME of a composite key spans every row sharing those columns.
        /// </summary>
        internal static Dictionary<string, object?> SelectPredicateColumns(
            Dictionary<string, object?> dbData, HashSet<string> clientColumns, IDbTable table)
        {
            var predicateData = dbData
                .Where(kv => clientColumns.Contains(kv.Key) || IsPrimaryKeyColumn(table, kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            MutationArgumentBinder.RequireCompleteKey(table, predicateData.Keys, "Delete");

            return predicateData;
        }

        // Soft-delete: the delete was transformed to UPDATE. The SET list carries ONLY
        // the columns a transformer stamped (soft-delete deleted_at/deleted_by, audit
        // updated_at/updated_by) — never a client-supplied column, or a delete predicate
        // like status:"archived" would be written into the row unconditionally. The WHERE
        // carries the client-supplied predicate columns, so a client predicate narrows
        // which rows are soft-deleted. Before-commit hooks and the write commit atomically.
        private static async Task<DeleteOutcome> ExecuteSoftDeleteAsync(
            IDbTable table, ISqlDialect dialect, MutationPipelineContext ctx,
            DbConnection conn, DbTransaction transaction,
            Dictionary<string, object?> dbData,
            HashSet<string> clientColumns,
            (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) additionalFilter,
            IDictionary<string, object?> mutationState)
        {
            var keyData = SelectPredicateColumns(dbData, clientColumns, table);
            var setData = dbData
                .Where(kv => !keyData.ContainsKey(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            if (keyData.Count == 0)
                throw new BifrostExecutionError(
                    "A soft delete requires a primary key or at least one predicate column to scope the affected rows.");

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var sql = MutationCommandExecutor.BuildUpdateSql(dialect, table, tableRef, setData.Keys, keyData, additionalFilter.WhereSuffix);
            var hookContext = HookContext(table, MutationType.Update, dbData, ctx, conn, transaction, dialect, mutationState);
            ApprovalInterceptMutationHook.SetLogicalMutationType(hookContext, MutationType.Delete);
            await MutationNotifier.RunBeforeCommitHooksAsync(ctx.Services, hookContext);
            if (ApprovalInterceptMutationHook.TryGetDivertMessage(hookContext, out var divertMessage))
                return new DeleteOutcome(0, divertMessage, MutationType.Update, dbData);
            var result = await MutationCommandExecutor.ExecuteNonQuery(conn, transaction, sql, dbData, additionalFilter.Parameters, ctx.CancellationToken);
            // Soft delete is modeled as an UPDATE; emit an update event (hook skips zero-row).
            await MutationNotifier.RunInTransactionHooksAsync(ctx.Services, hookContext, result, result);
            return new DeleteOutcome(result, null, MutationType.Update, dbData);
        }

        // Standard hard DELETE. WHERE is built from the client-supplied predicate
        // columns only (see SelectPredicateColumns). Before-commit hooks and the write
        // commit atomically.
        private static async Task<DeleteOutcome> ExecuteHardDeleteAsync(
            IDbTable table, ISqlDialect dialect, MutationPipelineContext ctx,
            DbConnection conn, DbTransaction transaction,
            Dictionary<string, object?> dbData,
            HashSet<string> clientColumns,
            (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) additionalFilter,
            IDictionary<string, object?> mutationState)
        {
            var deleteData = SelectPredicateColumns(dbData, clientColumns, table);

            if (deleteData.Count == 0)
                throw new BifrostExecutionError(
                    "A delete requires a primary key or at least one predicate column to scope the affected rows.");

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var sql = MutationCommandExecutor.BuildDeleteSql(dialect, table, tableRef, deleteData, additionalFilter.WhereSuffix);
            var hookContext = HookContext(table, MutationType.Delete, deleteData, ctx, conn, transaction, dialect, mutationState);
            await MutationNotifier.RunBeforeCommitHooksAsync(ctx.Services, hookContext);
            if (ApprovalInterceptMutationHook.TryGetDivertMessage(hookContext, out var divertMessage))
                return new DeleteOutcome(0, divertMessage, MutationType.Delete, deleteData);
            var result = await MutationCommandExecutor.ExecuteNonQuery(conn, transaction, sql, deleteData, additionalFilter.Parameters, ctx.CancellationToken);
            await MutationNotifier.RunInTransactionHooksAsync(ctx.Services, hookContext, result, result);
            return new DeleteOutcome(result, null, MutationType.Delete, deleteData);
        }
    }
}
