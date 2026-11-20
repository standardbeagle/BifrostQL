using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using static BifrostQL.Core.Resolvers.DbParameterBinder;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Executes an ordered list of <see cref="TreeSyncOperation"/> (produced by
/// <see cref="TreeSyncEngine"/>) inside a single transaction. Insert operations
/// run parent-first; each inserted row's primary key is captured and propagated
/// into the foreign-key (and polymorphic id) columns of its children before they
/// execute, so a nested insert links itself up automatically.
///
/// Each inserted row's PK is tracked BOTH per table GraphQL name (fallback) and
/// per operation instance (<see cref="TreeSyncOperation.InstanceId"/>). A child's
/// deferred FK resolves against its parent's specific instance
/// (<see cref="TreeSyncOperation.ParentInstanceId"/>) when the engine tagged one,
/// so a tree with several sibling parents of the same table — each owning their
/// own descendants — links every child to its own parent rather than collapsing
/// them onto the last-inserted row of that table.
///
/// Every operation is routed through the mutation-transformer pipeline (when one
/// is supplied) before its SQL is built, so soft-delete / authorization-policy /
/// audit-populate apply to nested and orphan operations exactly as they do to a
/// single-row mutation: a Delete of a soft-delete row is rewritten to an UPDATE,
/// a transformer's AdditionalFilter is ANDed onto the WHERE clause, audit columns
/// are stamped on insert/update, and any transformer error aborts the whole
/// transaction.
///
/// The transaction is expressed as SQL on the open connection (the dialect's
/// BEGIN/COMMIT/ROLLBACK keywords) rather than the ADO.NET DbTransaction API, so
/// the transaction boundary is visible in the emitted SQL. A save that records an
/// operation id runs instead on the operation's DbTransaction
/// (<see cref="ExecuteInScopeAsync"/>), which commits the tree with its record.
/// </summary>
public sealed class TreeSyncExecutor
{
    private readonly ISqlDialect _dialect;

    public TreeSyncExecutor(ISqlDialect dialect)
    {
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
    }

    /// <summary>
    /// Runs the operations and returns the primary key of the root (depth 0)
    /// insert, or null if the root was not an insert.
    /// </summary>
    /// <param name="operations">Ordered operations from the engine.</param>
    /// <param name="connFactory">Connection source.</param>
    /// <param name="transformers">
    /// Mutation-transformer pipeline. When null, operations execute verbatim
    /// (no soft-delete / policy / audit) — the pre-pipeline behavior, retained
    /// for the direct-executor integration tests.
    /// </param>
    /// <param name="model">Model for the transform context (required when <paramref name="transformers"/> is set).</param>
    /// <param name="userContext">Per-request user context for the transform context.</param>
    /// <param name="services">Request services for the transform context.</param>
    /// <param name="cancellationToken">The request's abort signal. Every statement observes it;
    /// the rollback does not, so an aborted tree still releases its transaction.</param>
    public async Task<object?> ExecuteAsync(
        IReadOnlyList<TreeSyncOperation> operations,
        IDbConnFactory connFactory,
        IMutationTransformers? transformers = null,
        IDbModel? model = null,
        IDictionary<string, object?>? userContext = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        if (operations.Count == 0)
            return null;
        GuardNotHistoryTargets(operations, model);
        GuardTreeSize(operations, RootTable(operations));
        return await RunInTreeTransactionAsync(connFactory, conn => RunOperationsAsync(
            operations, conn, transaction: null, MutationObserverContext.NewMutationState(),
            connFactory, transformers, model, userContext, services, recordsOperationId: false, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Loads the existing subtree for <paramref name="tree"/>, diffs it and writes the
    /// operations, all on one connection inside one transaction. The loader takes an update
    /// lock on every row it reads, so no other writer can change or delete a loaded row
    /// between the diff and the writes; on Postgres the root's lock also holds back a
    /// concurrent child INSERT (its FK check needs a key-share lock on the parent) until the
    /// sync commits; on SQL Server the child read's key-range lock does the same. Isolation
    /// stays READ COMMITTED. A deadlock victim reruns the whole transaction, up to
    /// <see cref="MaxDeadlockVictimAttempts"/> runs.
    /// </summary>
    public async Task<object?> SyncAsync(
        IDbTable table,
        Dictionary<string, object?> tree,
        TreeSyncStateLoader loader,
        TreeSyncEngine engine,
        IDbConnFactory connFactory,
        IMutationTransformers transformers,
        IDbModel model,
        IDictionary<string, object?> userContext,
        IServiceProvider? services,
        CancellationToken cancellationToken = default)
    {
        return await RetryDeadlockVictimAsync(() => RunInTreeTransactionAsync(connFactory, async conn =>
        {
            var existing = await loader.LoadAsync(table, tree, conn, cancellationToken);
            var operations = engine.ComputeOperations(table, tree, existing);
            GuardNotHistoryTargets(operations, model);
            GuardTreeSize(operations, table);
            if (operations.Count == 0)
                return new TreeRun(null, null);
            return await RunOperationsAsync(
                operations, conn, transaction: null, MutationObserverContext.NewMutationState(),
                connFactory, transformers, model, userContext, services, recordsOperationId: false, cancellationToken);
        }, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// How many times <see cref="SyncAsync"/> runs its whole transaction when the database
    /// picks it as a deadlock victim, the first run included. The sync's locks (SQL Server
    /// <c>UPDLOCK, HOLDLOCK</c> key ranges) can deadlock against a concurrent writer; the
    /// victim's transaction is already rolled back, so a rerun loads, diffs and writes from
    /// scratch.
    /// </summary>
    internal const int MaxDeadlockVictimAttempts = 3;

    // Reruns attempt while it fails as a deadlock victim (OperationIdExecution.IsDeadlock reads
    // the driver code: SQL Server 1205, PostgreSQL 40P01, MySQL 40001), at most
    // MaxDeadlockVictimAttempts runs in all. The last failure is rethrown as thrown.
    private static async Task<object?> RetryDeadlockVictimAsync(
        Func<Task<object?>> attempt, CancellationToken cancellationToken)
    {
        for (var run = 1; ; run++)
        {
            try
            {
                return await attempt();
            }
            catch (Exception ex) when (run < MaxDeadlockVictimAttempts
                                       && !cancellationToken.IsCancellationRequested
                                       && OperationId.OperationIdExecution.IsDeadlock(ex))
            {
            }
        }
    }

    // Opens a connection and runs body inside the tree's SQL-level transaction: commit on
    // success, rollback on any failure.
    private async Task<object?> RunInTreeTransactionAsync(
        IDbConnFactory connFactory, Func<DbConnection, Task<TreeRun>> body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var conn = connFactory.GetConnection();
        await conn.OpenAsync(cancellationToken);

        // Transaction control as SQL on the open connection (dialect keywords),
        // not the ADO.NET DbTransaction API — the boundary shows up in the SQL.
        await ExecuteRawAsync(conn, _dialect.BeginTransactionSql, cancellationToken);

        TreeRun run;
        try
        {
            run = await body(conn);
            await ExecuteRawAsync(conn, _dialect.CommitTransactionSql, cancellationToken);
        }
        catch (Exception ex)
        {
            try { await ExecuteRawAsync(conn, _dialect.RollbackTransactionSql, CancellationToken.None); } catch { /* surface the original error */ }
            // A request abort propagates as itself, like MutationCommandExecutor.RunInTransactionAsync.
            if (ex is OperationCanceledException)
                throw;
            throw BifrostExecutionError.FromDatabaseException(ex);
        }

        // Thrown AFTER the commit (outside the try, so it is not wrapped as a database error)
        // so the enqueued pending row(s) survive; never reported as success-as-applied.
        if (run.PendingApproval is not null)
            throw Approval.ApprovalInterceptMutationHook.PendingApprovalError(run.PendingApproval);
        return run.RootId;
    }

    /// <summary>
    /// Runs the operations on a mutation operation's shared transaction (a save that records an
    /// operation id): every node joins <paramref name="scope"/>, so the tree commits with the
    /// id's record or rolls back with it, and the per-transaction hook bag is the scope's. Commit
    /// and rollback belong to the scope. A node the approval gate diverts is refused with
    /// <see cref="OperationId.OperationIdExecution.UnsupportedCode"/>.
    /// </summary>
    internal async Task<object?> ExecuteInScopeAsync(
        IReadOnlyList<TreeSyncOperation> operations,
        OperationWriteScope scope,
        IDbConnFactory connFactory,
        IMutationTransformers transformers,
        IDbModel model,
        IDictionary<string, object?> userContext,
        IServiceProvider? services,
        CancellationToken cancellationToken = default)
    {
        if (operations.Count == 0)
            return null;
        GuardNotHistoryTargets(operations, model);
        GuardTreeSize(operations, RootTable(operations));

        object? rootId = null;
        await scope.JoinAsync(async (conn, transaction) =>
        {
            var run = await RunOperationsAsync(
                operations, conn, transaction, scope.TransactionState,
                connFactory, transformers, model, userContext, services, recordsOperationId: true, cancellationToken);
            rootId = run.RootId;
        });
        return rootId;
    }

    private readonly record struct TreeRun(object? RootId, string? PendingApproval);

    /// <summary>
    /// History targets take no client writes on ANY seam — a nested sync could otherwise smuggle
    /// a forged trail row in as a tree node. Checked up front, before a connection is even
    /// taken, so a rejected tree writes nothing. The raw direct-executor path (model == null, no
    /// transformer pipeline) has no model to resolve targets against; every GraphQL entry
    /// supplies one.
    /// </summary>
    private static void GuardNotHistoryTargets(IReadOnlyList<TreeSyncOperation> operations, IDbModel? model)
    {
        if (model == null)
            return;
        foreach (var op in operations)
            TableMutationPipeline.GuardNotHistoryTarget(op.Table, model);
    }

    /// <summary>
    /// <c>batch-max-size</c> on the tree's root table bounds a tree's writes as it bounds a batch's
    /// actions: a client's one request cannot fan out into an unbounded set. Checked before any
    /// write, so a refused tree writes nothing.
    /// </summary>
    private static void GuardTreeSize(IReadOnlyList<TreeSyncOperation> operations, IDbTable rootTable)
    {
        var maxNodes = BatchMutationPipeline.GetMaxBatchSize(rootTable);
        if (operations.Count > maxNodes)
            throw new BifrostExecutionError(
                $"Tree of {operations.Count} writes exceeds maximum allowed size of {maxNodes}.");
    }

    private static IDbTable RootTable(IReadOnlyList<TreeSyncOperation> operations)
        => (operations.FirstOrDefault(op => op.Depth == 0) ?? operations[0]).Table;

    /// <summary>
    /// Runs every operation, level by level, on <paramref name="conn"/> and
    /// <paramref name="transaction"/> (null under the tree's own SQL-level transaction).
    /// <paramref name="recordsOperationId"/> marks a tree that records an operation id: every
    /// chain run carries it, and an approval divert is refused rather than left pending.
    /// </summary>
    private async Task<TreeRun> RunOperationsAsync(
        IReadOnlyList<TreeSyncOperation> operations,
        DbConnection conn,
        DbTransaction? transaction,
        IDictionary<string, object?> treeTransactionState,
        IDbConnFactory connFactory,
        IMutationTransformers? transformers,
        IDbModel? model,
        IDictionary<string, object?>? userContext,
        IServiceProvider? services,
        bool recordsOperationId,
        CancellationToken cancellationToken)
    {
        // Known PK per parent table GraphQL name. Pre-seeded with parents that
        // already exist (update/delete/PK-bearing ops) so a new child can link to
        // an existing parent, then extended with each freshly inserted PK. This is
        // the fallback path for ops with no ParentInstanceId.
        var idsByTable = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in operations)
            SeedKnownId(op, idsByTable);

        // Known PK per INSERTED parent instance. This is the authoritative resolver
        // for a child's deferred FK: two sibling parents of the same table each own
        // their own children, and a table-name-keyed lookup alone would collapse
        // them (attaching the first parent's children to the second parent's PK).
        var idsByInstance = new Dictionary<string, object?>(StringComparer.Ordinal);

        object? rootId = null;
        string? pendingApproval = null;

        // One level at a time: a run of operations with the same verb, depth and table. Inserts
        // are ordered by depth, so every parent of a level was written by an earlier level.
        for (var start = 0; start < operations.Count;)
        {
            var first = operations[start];
            var end = start + 1;
            while (end < operations.Count
                   && operations[end].OperationType == first.OperationType
                   && operations[end].Depth == first.Depth
                   && ReferenceEquals(operations[end].Table, first.Table))
                end++;
            var level = new List<TreeSyncOperation>(end - start);
            for (var i = start; i < end; i++)
                level.Add(operations[i]);
            start = end;

            var setRows = new List<MutationSetRow>();
            // A level's inserts share one statement unless a per-row in-transaction hook applies:
            // such a hook is called before the next row's statement. The raw-executor path
            // (no model) runs no hooks at all.
            var insertsShareAStatement = first.OperationType == TreeSyncOperationType.Insert
                && MutationCommandExecutor.CanInsertMany(_dialect, first.Table)
                && !(model != null && MutationNotifier.AnyPerRowInTransactionHookApplies(services, first.Table));
            if (insertsShareAStatement)
            {
                var prepared = new List<PreparedNode>(level.Count);
                foreach (var op in level)
                {
                    var node = await PrepareNodeAsync(op);
                    if (node.DivertMessage is not null)
                        pendingApproval = node.DivertMessage;
                    else
                        prepared.Add(node);
                }
                await WriteInsertLevelAsync(prepared, setRows);
            }
            else
            {
                foreach (var op in level)
                {
                    var node = await PrepareNodeAsync(op);
                    if (node.DivertMessage is not null)
                    {
                        pendingApproval = node.DivertMessage;
                        continue;
                    }
                    var (opResult, affectedRows) = await WriteNodeAsync(node);
                    // After the write, still inside the same transaction: the CDC event
                    // and the history row are written here, paired with this operation's
                    // before-commit capture through the shared context's MutationState.
                    if (node.HookContext != null)
                        await MutationNotifier.RunPerRowInTransactionHooksAsync(services, node.HookContext, opResult, affectedRows);
                    AddSetRow(setRows, node, opResult, affectedRows);
                }
            }

            if (model != null && setRows.Count > 0)
                await MutationNotifier.RunSetAwareInTransactionHooksAsync(services, new MutationSetContext
                {
                    Table = first.Table,
                    Rows = setRows,
                    UserContext = userContext ?? new Dictionary<string, object?>(),
                    Connection = conn,
                    Transaction = transaction,
                    Model = model,
                    Dialect = _dialect,
                    ConnFactory = connFactory,
                    TransactionState = treeTransactionState,
                    CancellationToken = cancellationToken,
                });
        }

        return new TreeRun(rootId, pendingApproval);

        // Links the node to its parents, runs its transformer chain and its before-commit hooks,
        // and reports an approval divert; writes nothing.
        async Task<PreparedNode> PrepareNodeAsync(TreeSyncOperation op)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Resolve FK links BEFORE transforming so audit / policy see the
            // final data (including the parent PK inserted by an earlier level).
            ResolveForeignKeys(op, idsByTable, idsByInstance);

            var mutationType = MapMutationType(op.OperationType);
            var logicalMutationType = mutationType;
            var conflictOnNoRows = false;
            var data = op.Data;
            // The columns this operation actually carried, snapshotted (in DB-name
            // space) BEFORE the transformer chain runs. These — plus the primary key
            // — are the only legitimate WHERE-predicate columns for a delete. Columns
            // a transformer stamps afterwards (audit updated_at/updated_by) would
            // otherwise AND a never-matching term into the WHERE and silently affect
            // zero rows. Same contract as the single-row and batch pipelines.
            var clientColumns = new HashSet<string>(
                op.Data.Keys.Select(k => ToDbColumnName(op.Table, k)),
                StringComparer.OrdinalIgnoreCase);
            (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) additionalFilter
                = ("", Array.Empty<SqlParameterInfo>());
            // This node's per-action bag, shared by its transformer chain and both hook phases.
            var nodeState = MutationObserverContext.NewMutationState();

            if (transformers != null)
            {
                var ctx = new MutationTransformContext
                {
                    Model = model ?? throw new ArgumentNullException(nameof(model),
                        "A model is required when a mutation-transformer pipeline is supplied."),
                    UserContext = userContext ?? new Dictionary<string, object?>(),
                    Services = services,
                    // The tree's connection and transaction: null on the tree's own
                    // connection, whose transaction is SQL-level BEGIN/COMMIT (commands run
                    // on the ambient transaction); the operation's DbTransaction in a scope.
                    Connection = conn,
                    Transaction = transaction,
                    Dialect = _dialect,
                    RecordsOperationId = recordsOperationId,
                    MutationState = nodeState,
                    CancellationToken = cancellationToken,
                };
                var result = await transformers.TransformAsync(op.Table, mutationType, data, ctx);
                result.ThrowIfDenied();

                // Honor a rewritten type (Delete → Update for soft-delete),
                // the rewritten data (audit-populate / enum mapping), and the
                // transformer's row-scope / IS-NULL guard filter.
                mutationType = result.MutationType;
                conflictOnNoRows = result.ConflictOnNoRows;
                data = result.Data;
                additionalFilter = MutationCommandExecutor.RenderAdditionalFilter(result.AdditionalFilter, _dialect);
            }

            // Before-commit hooks run before this operation's write, on the SAME connection and
            // transaction (null under the tree's own SQL BEGIN/COMMIT). A veto throws, and the
            // tree's transaction rolls the whole tree back — a nested sync is one transaction, so
            // a rejected node cannot leave its siblings committed. Only when the transformer
            // pipeline is active: a model is required to resolve what a hook writes into, and the
            // raw-executor test path supplies none. The context — and the state bag pairing the
            // two hook phases — is scoped per operation in the tree, never across operations;
            // only the per-transaction bag spans them.
            MutationObserverContext? hookContext = null;
            string? divertMessage = null;
            if (model != null)
            {
                hookContext = new MutationObserverContext
                {
                    Table = op.Table,
                    MutationType = mutationType,
                    Data = data,
                    Result = null,
                    UserContext = userContext ?? new Dictionary<string, object?>(),
                    Connection = conn,
                    Transaction = transaction,
                    Model = model,
                    Dialect = _dialect,
                    ConnFactory = connFactory,
                    CancellationToken = cancellationToken,
                    MutationState = nodeState,
                    TransactionState = treeTransactionState,
                };
                Approval.ApprovalInterceptMutationHook.SetLogicalMutationType(hookContext, logicalMutationType);
                await MutationNotifier.RunBeforeCommitHooksAsync(services, hookContext);

                // Approval gate: this operation was enqueued as a pending change on the
                // tree's transaction. Skip its write (and after-write hooks) so nothing
                // applies to the gated table; the tree commits the pending row(s) and
                // surfaces pending-approval after the commit. A gated node never inserts,
                // so a child's deferred FK to it stays unresolved (its intent is enqueued,
                // not applied) — consistent with "no SQL reaches a gated table".
                if (Approval.ApprovalInterceptMutationHook.TryGetDivertMessage(hookContext, out var message))
                {
                    // A tree that records an operation id commits its record or nothing, so it
                    // cannot also leave a pending change behind: the pending error would roll
                    // the pending row back with the record. Refused, and the whole tree with it.
                    if (recordsOperationId)
                        throw new BifrostExecutionError("This write requires approval, so it cannot record an operation id.")
                        { ErrorCode = OperationId.OperationIdExecution.UnsupportedCode };
                    divertMessage = message;
                }
            }

            return new PreparedNode(op, mutationType, data, clientColumns, additionalFilter,
                conflictOnNoRows, nodeState, hookContext, divertMessage);
        }

        // One node's own statement.
        async Task<(object? Result, int AffectedRows)> WriteNodeAsync(PreparedNode node)
        {
            var op = node.Op;
            switch (node.MutationType)
            {
                case MutationType.Insert:
                    var id = await ExecuteInsertAsync(conn, transaction, op.Table, node.Data, cancellationToken);
                    RecordInsertedKey(op, id);
                    return (id, 1);
                case MutationType.Update:
                    var updated = await ExecuteUpdateAsync(conn, transaction, op.Table, node.Data, node.AdditionalFilter, cancellationToken);
                    MutationCommandExecutor.EnsureAffectedRows(
                        updated, node.ConflictOnNoRows, op.Inferred,
                        $"{op.Table.TableSchema}.{op.Table.DbName}");
                    return (updated, updated);
                case MutationType.Delete:
                    var deleteData = TableMutationPipeline.SelectPredicateColumns(node.Data, node.ClientColumns, op.Table);
                    if (deleteData.Count == 0)
                        throw new BifrostExecutionError(
                            "A delete requires a primary key or at least one predicate column to scope the affected rows.");
                    var deleted = await ExecuteDeleteAsync(conn, transaction, op.Table, deleteData, node.AdditionalFilter, cancellationToken);
                    MutationCommandExecutor.EnsureAffectedRows(
                        deleted, node.ConflictOnNoRows, op.Inferred,
                        $"{op.Table.TableSchema}.{op.Table.DbName}");
                    return (deleted, deleted);
                default:
                    return (null, 0);
            }
        }

        // A level's inserts: one multi-row INSERT … RETURNING per column set, keys mapped back to
        // their nodes in row order so each child links to its own parent. A transformer that
        // rewrote a node's verb leaves the level to per-node statements.
        async Task WriteInsertLevelAsync(List<PreparedNode> prepared, List<MutationSetRow> setRows)
        {
            if (prepared.Any(n => n.MutationType != MutationType.Insert))
            {
                foreach (var node in prepared)
                {
                    var (opResult, affectedRows) = await WriteNodeAsync(node);
                    AddSetRow(setRows, node, opResult, affectedRows);
                }
                return;
            }
            var keys = new object?[prepared.Count];
            foreach (var group in Enumerable.Range(0, prepared.Count).GroupBy(i => MutationCommandExecutor.ColumnSetKey(prepared[i].Data)))
            {
                var indices = group.ToList();
                var groupKeys = await MutationCommandExecutor.ExecuteInsertMany(
                    conn, transaction, _dialect, prepared[indices[0]].Op.Table, prepared[indices[0]].Data.Keys.ToList(),
                    indices.Select(i => (IReadOnlyDictionary<string, object?>)prepared[i].Data).ToList(), cancellationToken);
                for (var g = 0; g < indices.Count; g++)
                    keys[indices[g]] = groupKeys[g];
            }
            for (var i = 0; i < prepared.Count; i++)
            {
                RecordInsertedKey(prepared[i].Op, keys[i]);
                AddSetRow(setRows, prepared[i], keys[i], 1);
            }
        }

        void RecordInsertedKey(TreeSyncOperation op, object? id)
        {
            idsByTable[op.Table.GraphQlName] = id;
            idsByInstance[op.InstanceId] = id;
            if (op.Depth == 0)
                rootId = id;
        }

        static void AddSetRow(List<MutationSetRow> setRows, PreparedNode node, object? opResult, int affectedRows)
        {
            if (affectedRows <= 0)
                return;
            setRows.Add(new MutationSetRow(
                node.MutationType, node.Data,
                node.MutationType == MutationType.Insert ? opResult : MutationCommandExecutor.KeyOf(node.Op.Table, node.Data),
                node.NodeState));
        }
    }

    /// <summary>A node after its chain and before-commit hooks, ready to write (or diverted).</summary>
    private sealed record PreparedNode(
        TreeSyncOperation Op,
        MutationType MutationType,
        Dictionary<string, object?> Data,
        HashSet<string> ClientColumns,
        (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) AdditionalFilter,
        bool ConflictOnNoRows,
        IDictionary<string, object?> NodeState,
        MutationObserverContext? HookContext,
        string? DivertMessage);

    private static MutationType MapMutationType(TreeSyncOperationType type) => type switch
    {
        TreeSyncOperationType.Insert => MutationType.Insert,
        TreeSyncOperationType.Update => MutationType.Update,
        TreeSyncOperationType.Delete => MutationType.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown tree-sync operation type."),
    };

    private static void ResolveForeignKeys(
        TreeSyncOperation op,
        Dictionary<string, object?> idsByTable,
        Dictionary<string, object?> idsByInstance)
    {
        // Prefer the specific parent INSTANCE PK when the engine tagged one — that
        // is the only unambiguous source when several same-table parents each own
        // children. Fall back to the table-name lookup for ops without a tagged
        // parent instance (root, existing-parent links, or manually built ops).
        object? instancePk = null;
        var haveInstancePk = op.ParentInstanceId != null
            && idsByInstance.TryGetValue(op.ParentInstanceId, out instancePk);

        foreach (var (fkColumn, parentGraphQlName) in op.ForeignKeyAssignments)
        {
            if (haveInstancePk)
                op.Data[fkColumn] = instancePk;
            else if (idsByTable.TryGetValue(parentGraphQlName, out var parentId))
                op.Data[fkColumn] = parentId;
        }
    }

    // Records the PK of a parent that already exists (single-key tables) so new
    // children can link to it even though it is not freshly inserted this run.
    private static void SeedKnownId(TreeSyncOperation op, Dictionary<string, object?> idsByTable)
    {
        if (op.OperationType == TreeSyncOperationType.Insert)
            return;
        var keyCols = op.Table.KeyColumns.ToList();
        if (keyCols.Count != 1)
            return;
        if (op.Data.TryGetValue(keyCols[0].ColumnName, out var pk) && pk != null)
            idsByTable[op.Table.GraphQlName] = pk;
    }

    private async Task<object?> ExecuteInsertAsync(DbConnection conn, DbTransaction? transaction, IDbTable table, Dictionary<string, object?> data,
        CancellationToken cancellationToken)
    {
        var tableRef = _dialect.TableReference(table.TableSchema, table.DbName);
        var columns = string.Join(",", data.Keys.Select(k => _dialect.EscapeIdentifier(k)));
        var values = string.Join(",", data.Keys.Select(k => ValuePlaceholder(_dialect, table, k)));
        var returning = _dialect.ReturningIdentityClauseFor(table.KeyColumns.Select(k => k.ColumnName).ToList());
        var sql = returning != null
            ? $"INSERT INTO {tableRef}({columns}) VALUES({values}){returning};"
            : $"INSERT INTO {tableRef}({columns}) VALUES({values});SELECT {_dialect.LastInsertedIdentity} ID;";

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        AddParameters(cmd, data);
        return HandleDecimals(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    // Returns the affected-row count so the caller can skip emitting a CDC event for a
    // zero-row no-op. Returns 0 for the degenerate no-key / no-set-columns case.
    private async Task<int> ExecuteUpdateAsync(DbConnection conn, DbTransaction? transaction, IDbTable table, Dictionary<string, object?> data,
        (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) additionalFilter, CancellationToken cancellationToken)
    {
        var (keyData, setData) = MutationArgumentBinder.SplitKeyAndSet(table, data);
        if (keyData.Count == 0 || setData.Count == 0)
            return 0;

        var tableRef = _dialect.TableReference(table.TableSchema, table.DbName);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = MutationCommandExecutor.BuildUpdateSql(_dialect, table, tableRef, setData.Keys, keyData, additionalFilter.WhereSuffix);
        AddParameters(cmd, data);
        AddExtraParameters(cmd, additionalFilter.Parameters);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> ExecuteDeleteAsync(DbConnection conn, DbTransaction? transaction, IDbTable table, Dictionary<string, object?> data,
        (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) additionalFilter, CancellationToken cancellationToken)
    {
        if (data.Count == 0)
            return 0;

        var tableRef = _dialect.TableReference(table.TableSchema, table.DbName);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = MutationCommandExecutor.BuildDeleteSql(_dialect, table, tableRef, data, additionalFilter.WhereSuffix);
        AddParameters(cmd, data);
        AddExtraParameters(cmd, additionalFilter.Parameters);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // Issues a plain (parameterless) statement on the open connection. Used for
    // the dialect's transaction-control keywords.
    private static async Task ExecuteRawAsync(DbConnection conn, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

}
