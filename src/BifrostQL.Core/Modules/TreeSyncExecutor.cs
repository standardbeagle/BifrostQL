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
    public async Task<object?> ExecuteAsync(
        IReadOnlyList<TreeSyncOperation> operations,
        IDbConnFactory connFactory)
    {
        if (operations.Count == 0)
            return null;

        await using var conn = connFactory.GetConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Known PK per parent table GraphQL name. Pre-seeded with parents that
        // already exist (update/delete/PK-bearing ops) so a new child can link to
        // an existing parent, then extended with each freshly inserted PK.
        var idsByTable = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in operations)
            SeedKnownId(op, idsByTable);

        object? rootId = null;

        try
        {
            foreach (var op in operations)
            {
                ResolveForeignKeys(op, idsByTable);

                switch (op.OperationType)
                {
                    case TreeSyncOperationType.Insert:
                        var id = await ExecuteInsertAsync(conn, tx, op);
                        idsByTable[op.Table.GraphQlName] = id;
                        if (op.Depth == 0)
                            rootId = id;
                        break;
                    case TreeSyncOperationType.Update:
                        await ExecuteUpdateAsync(conn, tx, op);
                        break;
                    case TreeSyncOperationType.Delete:
                        await ExecuteDeleteAsync(conn, tx, op);
                        break;
                }
            }

            await tx.CommitAsync();
            return rootId;
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(); } catch { /* surface the original error */ }
            throw new BifrostExecutionError(ex.Message, ex);
        }
    }

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

    private async Task<object?> ExecuteInsertAsync(DbConnection conn, DbTransaction tx, TreeSyncOperation op)
    {
        var tableRef = _dialect.TableReference(op.Table.TableSchema, op.Table.DbName);
        var columns = string.Join(",", op.Data.Keys.Select(k => _dialect.EscapeIdentifier(k)));
        var values = string.Join(",", op.Data.Keys.Select(k => $"@{k}"));
        var returning = _dialect.ReturningIdentityClause;
        var sql = returning != null
            ? $"INSERT INTO {tableRef}({columns}) VALUES({values}){returning};"
            : $"INSERT INTO {tableRef}({columns}) VALUES({values});SELECT {_dialect.LastInsertedIdentity} ID;";

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        AddParameters(cmd, op.Data);
        return HandleDecimals(await cmd.ExecuteScalarAsync());
    }

    private async Task ExecuteUpdateAsync(DbConnection conn, DbTransaction tx, TreeSyncOperation op)
    {
        var keyData = op.Data.Where(kv => IsKey(op.Table, kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var setData = op.Data.Where(kv => !IsKey(op.Table, kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (keyData.Count == 0 || setData.Count == 0)
            return;

        var tableRef = _dialect.TableReference(op.Table.TableSchema, op.Table.DbName);
        var setClause = string.Join(",", setData.Select(kv => $"{_dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
        var whereClause = string.Join(" AND ", keyData.Select(kv => $"{_dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause};";
        AddParameters(cmd, op.Data);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ExecuteDeleteAsync(DbConnection conn, DbTransaction tx, TreeSyncOperation op)
    {
        if (op.Data.Count == 0)
            return;

        var tableRef = _dialect.TableReference(op.Table.TableSchema, op.Table.DbName);
        var whereClause = string.Join(" AND ", op.Data.Select(kv => $"{_dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {tableRef} WHERE {whereClause};";
        AddParameters(cmd, op.Data);
        await cmd.ExecuteNonQueryAsync();
    }

    private static bool IsKey(IDbTable table, string column)
        => table.ColumnLookup.TryGetValue(column, out var col) && col.IsPrimaryKey;
}
