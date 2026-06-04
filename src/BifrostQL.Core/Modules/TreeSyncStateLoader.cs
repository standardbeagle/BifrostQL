using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Loads the existing database state for a submitted object tree so
/// <see cref="TreeSyncEngine"/> can reconcile it (insert new rows, update changed
/// rows, delete orphaned rows). The loaded shape mirrors the submitted tree:
/// scalar columns plus child collections keyed by the same multi-link key the
/// engine reads.
///
/// Only the child collections the client actually included are loaded — an
/// omitted collection is never fetched, so it is never diffed and never
/// orphan-deleted. Polymorphic child collections are filtered by both the id
/// column and the discriminator, so reconciling one parent's notes can never
/// touch another parent's (or another entity type's) rows.
///
/// Single-column primary keys only; composite-key roots return null (the engine
/// then treats the tree as a fresh insert).
/// </summary>
public sealed class TreeSyncStateLoader
{
    private readonly ISqlDialect _dialect;
    private readonly int _maxDepth;

    public TreeSyncStateLoader(ISqlDialect dialect, int maxDepth = 3)
    {
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _maxDepth = maxDepth;
    }

    /// <summary>
    /// Returns the existing subtree for the submitted root, or null when the root
    /// has no primary key, the row does not exist, or the key is composite.
    /// </summary>
    public async Task<Dictionary<string, object?>?> LoadAsync(
        IDbTable table,
        Dictionary<string, object?> submitted,
        IDbConnFactory connFactory)
    {
        var keyCol = SingleKey(table);
        if (keyCol == null)
            return null;
        if (!TryGetValueCI(submitted, keyCol.ColumnName, out var pk) || pk == null)
            return null;

        await using var conn = connFactory.GetConnection();
        await conn.OpenAsync();

        var row = await LoadRowAsync(table, keyCol, pk, conn);
        if (row == null)
            return null;

        await PopulateChildrenAsync(table, row, ChildLinkKeys(submitted, table), conn, depth: 0);
        return row;
    }

    private async Task PopulateChildrenAsync(
        IDbTable table,
        Dictionary<string, object?> row,
        ISet<string> includedLinks,
        DbConnection conn,
        int depth)
    {
        if (depth + 1 >= _maxDepth)
            return;

        var keyCol = SingleKey(table);
        if (keyCol == null || !row.TryGetValue(keyCol.ColumnName, out var parentPk) || parentPk == null)
            return;

        foreach (var (linkKey, link) in table.MultiLinks)
        {
            if (!includedLinks.Contains(linkKey))
                continue;

            var childTable = link.ChildTable;
            var fkColumn = link.ChildId?.ColumnName;
            if (fkColumn == null)
                continue;

            var children = await LoadChildrenAsync(childTable, fkColumn, parentPk, link.TypePredicate, conn);

            // Grandchildren are loaded only for the links any of these children
            // would themselves carry — approximated by the union across the rows.
            var grandLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
                await PopulateChildrenAsync(childTable, child, grandLinks, conn, depth + 1);

            row[linkKey] = children;
        }
    }

    private async Task<Dictionary<string, object?>?> LoadRowAsync(
        IDbTable table, ColumnDto keyCol, object pk, DbConnection conn)
    {
        var rows = await QueryAsync(table,
            $"{_dialect.EscapeIdentifier(keyCol.ColumnName)} = @pk",
            new Dictionary<string, object?> { ["@pk"] = pk },
            conn);
        return rows.Count > 0 ? rows[0] : null;
    }

    private async Task<List<Dictionary<string, object?>>> LoadChildrenAsync(
        IDbTable childTable, string fkColumn, object parentPk,
        LinkConstantPredicate? predicate, DbConnection conn)
    {
        var where = $"{_dialect.EscapeIdentifier(fkColumn)} = @fk";
        var parameters = new Dictionary<string, object?> { ["@fk"] = parentPk };
        if (predicate != null)
        {
            where += $" AND {_dialect.EscapeIdentifier(predicate.Column.ColumnName)} = @disc";
            parameters["@disc"] = predicate.Value;
        }
        return await QueryAsync(childTable, where, parameters, conn);
    }

    private async Task<List<Dictionary<string, object?>>> QueryAsync(
        IDbTable table, string whereClause, Dictionary<string, object?> parameters, DbConnection conn)
    {
        var columns = table.Columns.ToList();
        var columnSql = string.Join(", ", columns.Select(c => _dialect.EscapeIdentifier(c.ColumnName)));
        var tableRef = _dialect.TableReference(table.TableSchema, table.DbName);
        var sql = $"SELECT {columnSql} FROM {tableRef} WHERE {whereClause}";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        var results = new List<Dictionary<string, object?>>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < columns.Count; i++)
                {
                    var value = reader.GetValue(i);
                    row[columns[i].ColumnName] = value is DBNull ? null : value;
                }
                results.Add(row);
            }
        }
        catch (Exception ex)
        {
            throw new BifrostExecutionError(ex.Message, ex);
        }
        return results;
    }

    private static ColumnDto? SingleKey(IDbTable table)
    {
        var keys = table.KeyColumns.ToList();
        return keys.Count == 1 ? keys[0] : null;
    }

    // Multi-link keys present in the submitted node (the collections the client
    // chose to reconcile).
    private static HashSet<string> ChildLinkKeys(Dictionary<string, object?> submitted, IDbTable table)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var linkKey in table.MultiLinks.Keys)
        {
            if (TryGetValueCI(submitted, linkKey, out _))
                keys.Add(linkKey);
        }
        return keys;
    }

    private static bool TryGetValueCI(Dictionary<string, object?> dict, string key, out object? value)
    {
        if (dict.TryGetValue(key, out value))
            return true;
        foreach (var kvp in dict)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }
        value = null;
        return false;
    }
}
