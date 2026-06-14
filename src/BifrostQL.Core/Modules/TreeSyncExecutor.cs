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
/// The parent key is tracked per table GraphQL name, matching the engine's
/// <see cref="TreeSyncOperation.ForeignKeyAssignments"/> contract. This is exact
/// for a single root with one level of children (the common case); a tree with
/// several sibling parents of the same table each owning their own descendants is
/// not disambiguated — a known limitation shared with the engine's planning model.
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
        // an existing parent, then extended with each freshly inserted PK.
        var idsByTable = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in operations)
            SeedKnownId(op, idsByTable);

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
                ResolveForeignKeys(op, idsByTable);

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
                    additionalFilter = RenderAdditionalFilter(result.AdditionalFilter, _dialect);
                }

                switch (mutationType)
                {
                    case MutationType.Insert:
                        var id = await ExecuteInsertAsync(conn, op.Table, data);
                        idsByTable[op.Table.GraphQlName] = id;
                        if (op.Depth == 0)
                            rootId = id;
                        break;
                    case MutationType.Update:
                        await ExecuteUpdateAsync(conn, op.Table, data, additionalFilter);
                        break;
                    case MutationType.Delete:
                        await ExecuteDeleteAsync(conn, op.Table, data, additionalFilter);
                        break;
                }
            }

            await ExecuteRawAsync(conn, _dialect.CommitTransactionSql);
            return rootId;
        }
        catch (Exception ex)
        {
            try { await ExecuteRawAsync(conn, _dialect.RollbackTransactionSql); } catch { /* surface the original error */ }
            throw new BifrostExecutionError(ex.Message, ex);
        }
    }


    private static MutationType MapMutationType(TreeSyncOperationType type) => type switch
    {
        TreeSyncOperationType.Insert => MutationType.Insert,
        TreeSyncOperationType.Update => MutationType.Update,
        TreeSyncOperationType.Delete => MutationType.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown tree-sync operation type."),
    };

    private static void ResolveForeignKeys(TreeSyncOperation op, Dictionary<string, object?> idsByTable)
    {
        foreach (var (fkColumn, parentGraphQlName) in op.ForeignKeyAssignments)
        {
            if (idsByTable.TryGetValue(parentGraphQlName, out var parentId))
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

    private async Task ExecuteUpdateAsync(DbConnection conn, IDbTable table, Dictionary<string, object?> data,
        (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) additionalFilter)
    {
        var keyData = data.Where(kv => IsKey(table, kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var setData = data.Where(kv => !IsKey(table, kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (keyData.Count == 0 || setData.Count == 0)
            return;

        var tableRef = _dialect.TableReference(table.TableSchema, table.DbName);
        var setClause = string.Join(",", setData.Select(kv => SetAssignment(_dialect, table, kv.Key)));
        var whereClause = string.Join(" AND ", keyData.Select(kv => $"{_dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause}{additionalFilter.WhereSuffix};";
        AddParameters(cmd, data);
        AddExtraParameters(cmd, additionalFilter.Parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ExecuteDeleteAsync(DbConnection conn, IDbTable table, Dictionary<string, object?> data,
        (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) additionalFilter)
    {
        if (data.Count == 0)
            return;

        var tableRef = _dialect.TableReference(table.TableSchema, table.DbName);
        var whereClause = string.Join(" AND ", data.Select(kv => $"{_dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {tableRef} WHERE {whereClause}{additionalFilter.WhereSuffix};";
        AddParameters(cmd, data);
        AddExtraParameters(cmd, additionalFilter.Parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    // Issues a plain (parameterless) statement on the open connection. Used for
    // the dialect's transaction-control keywords.
    private static async Task ExecuteRawAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // Renders MutationTransformResult.AdditionalFilter into an AND-prefixed WHERE
    // suffix and its bound parameters — same logic as the single-row resolver's
    // RenderAdditionalFilter (policy row-scope, soft-delete IS NULL). Returns an
    // empty suffix when no transformer contributed a filter.
    private static (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) RenderAdditionalFilter(
        TableFilter? filter, ISqlDialect dialect)
    {
        if (filter == null)
            return ("", Array.Empty<SqlParameterInfo>());

        var parameters = new SqlParameterCollection();
        var rendered = filter.RenderForMutation(dialect, parameters);
        return ($" AND ({rendered.Sql})", parameters.Parameters);
    }

    private static bool IsKey(IDbTable table, string column)
        => table.ColumnLookup.TryGetValue(column, out var col) && col.IsPrimaryKey;
}
