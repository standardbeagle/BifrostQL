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
/// the transaction boundary is visible in the emitted SQL.
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
    public async Task<object?> ExecuteAsync(
        IReadOnlyList<TreeSyncOperation> operations,
        IDbConnFactory connFactory,
        IMutationTransformers? transformers = null,
        IDbModel? model = null,
        IDictionary<string, object?>? userContext = null,
        IServiceProvider? services = null)
    {
        if (operations.Count == 0)
            return null;

        await using var conn = connFactory.GetConnection();
        await conn.OpenAsync();

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

        // Transaction control as SQL on the open connection (dialect keywords),
        // not the ADO.NET DbTransaction API — the boundary shows up in the SQL.
        await ExecuteRawAsync(conn, _dialect.BeginTransactionSql);

        try
        {
            foreach (var op in operations)
            {
                // Resolve FK links BEFORE transforming so audit / policy see the
                // final data (including the parent PK just inserted this run).
                ResolveForeignKeys(op, idsByTable, idsByInstance);

                var mutationType = MapMutationType(op.OperationType);
                var data = op.Data;
                (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) additionalFilter
                    = ("", Array.Empty<SqlParameterInfo>());

                if (transformers != null)
                {
                    var ctx = new MutationTransformContext
                    {
                        Model = model ?? throw new ArgumentNullException(nameof(model),
                            "A model is required when a mutation-transformer pipeline is supplied."),
                        UserContext = userContext ?? new Dictionary<string, object?>(),
                        Services = services,
                    };
                    var result = await transformers.TransformAsync(op.Table, mutationType, data, ctx);
                    if (result.Errors.Length > 0)
                        throw new BifrostExecutionError(string.Join("; ", result.Errors));

                    // Honor a rewritten type (Delete → Update for soft-delete),
                    // the rewritten data (audit-populate / enum mapping), and the
                    // transformer's row-scope / IS-NULL guard filter.
                    mutationType = result.MutationType;
                    data = result.Data;
                    additionalFilter = MutationCommandExecutor.RenderAdditionalFilter(result.AdditionalFilter, _dialect);
                }

                // Before-commit hooks run immediately before this operation's write, on the
                // SAME connection (whose transaction is managed with SQL BEGIN/COMMIT here,
                // so no DbTransaction object exists to pass). A veto throws, and the catch
                // below rolls the whole tree back — a nested sync is one transaction, so a
                // rejected node cannot leave its siblings committed. Only when the
                // transformer pipeline is active: a model is required to resolve what a hook
                // writes into, and the raw-executor test path supplies none. The context —
                // and the state bag pairing the two hook phases — is scoped per operation
                // in the tree, never across operations.
                MutationObserverContext? hookContext = null;
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
                        Transaction = null,
                        Model = model,
                        Dialect = _dialect,
                        MutationState = MutationObserverContext.NewMutationState(),
                    };
                    await MutationNotifier.RunBeforeCommitHooksAsync(services, hookContext);
                }

                object? opResult;
                switch (mutationType)
                {
                    case MutationType.Insert:
                        var id = await ExecuteInsertAsync(conn, op.Table, data);
                        idsByTable[op.Table.GraphQlName] = id;
                        idsByInstance[op.InstanceId] = id;
                        if (op.Depth == 0)
                            rootId = id;
                        opResult = id;
                        break;
                    case MutationType.Update:
                        opResult = await ExecuteUpdateAsync(conn, op.Table, data, additionalFilter);
                        break;
                    case MutationType.Delete:
                        opResult = await ExecuteDeleteAsync(conn, op.Table, data, additionalFilter);
                        break;
                    default:
                        opResult = null;
                        break;
                }

                // After the write, still inside the same SQL-level transaction: the CDC event
                // and the history row are written here, paired with this operation's
                // before-commit capture through the shared context's MutationState.
                if (hookContext != null)
                    await MutationNotifier.RunInTransactionHooksAsync(services, hookContext, opResult);
            }

            await ExecuteRawAsync(conn, _dialect.CommitTransactionSql);
            return rootId;
        }
        catch (Exception ex)
        {
            try { await ExecuteRawAsync(conn, _dialect.RollbackTransactionSql); } catch { /* surface the original error */ }
            throw BifrostExecutionError.FromDatabaseException(ex);
        }
    }


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

    private async Task<object?> ExecuteInsertAsync(DbConnection conn, IDbTable table, Dictionary<string, object?> data)
    {
        var tableRef = _dialect.TableReference(table.TableSchema, table.DbName);
        var columns = string.Join(",", data.Keys.Select(k => _dialect.EscapeIdentifier(k)));
        var values = string.Join(",", data.Keys.Select(k => ValuePlaceholder(_dialect, table, k)));
        var returning = _dialect.ReturningIdentityClauseFor(table.KeyColumns.Select(k => k.ColumnName).ToList());
        var sql = returning != null
            ? $"INSERT INTO {tableRef}({columns}) VALUES({values}){returning};"
            : $"INSERT INTO {tableRef}({columns}) VALUES({values});SELECT {_dialect.LastInsertedIdentity} ID;";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddParameters(cmd, data);
        return HandleDecimals(await cmd.ExecuteScalarAsync());
    }

    // Returns the affected-row count so the caller can skip emitting a CDC event for a
    // zero-row no-op. Returns 0 for the degenerate no-key / no-set-columns case.
    private async Task<int> ExecuteUpdateAsync(DbConnection conn, IDbTable table, Dictionary<string, object?> data,
        (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) additionalFilter)
    {
        var keyData = data.Where(kv => IsKey(table, kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var setData = data.Where(kv => !IsKey(table, kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (keyData.Count == 0 || setData.Count == 0)
            return 0;

        var tableRef = _dialect.TableReference(table.TableSchema, table.DbName);
        var setClause = string.Join(",", setData.Select(kv => SetAssignment(_dialect, table, kv.Key)));
        var whereClause = string.Join(" AND ", keyData.Select(kv => $"{_dialect.EscapeIdentifier(kv.Key)}=@{SqlParameterNames.Sanitize(kv.Key)}"));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause}{additionalFilter.WhereSuffix};";
        AddParameters(cmd, data);
        AddExtraParameters(cmd, additionalFilter.Parameters);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> ExecuteDeleteAsync(DbConnection conn, IDbTable table, Dictionary<string, object?> data,
        (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) additionalFilter)
    {
        if (data.Count == 0)
            return 0;

        var tableRef = _dialect.TableReference(table.TableSchema, table.DbName);
        var whereClause = string.Join(" AND ", data.Select(kv => $"{_dialect.EscapeIdentifier(kv.Key)}=@{SqlParameterNames.Sanitize(kv.Key)}"));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {tableRef} WHERE {whereClause}{additionalFilter.WhereSuffix};";
        AddParameters(cmd, data);
        AddExtraParameters(cmd, additionalFilter.Parameters);
        return await cmd.ExecuteNonQueryAsync();
    }

    // Issues a plain (parameterless) statement on the open connection. Used for
    // the dialect's transaction-control keywords.
    private static async Task ExecuteRawAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static bool IsKey(IDbTable table, string column)
        => table.ColumnLookup.TryGetValue(column, out var col) && col.IsPrimaryKey;
}
