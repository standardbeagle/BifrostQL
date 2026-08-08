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
    private readonly IDbModel _model;
    private readonly IDictionary<string, object?> _userContext;
    private readonly IServiceProvider? _services;

    /// <summary>
    /// <paramref name="model"/> and <paramref name="userContext"/> are REQUIRED: they
    /// are what the loader needs to apply each table's read chain (row filter, column
    /// read guard, crypto projection) to its read.
    ///
    /// They used to be optional "so existing callers keep compiling", and the one
    /// production caller — <c>DbTableMutateResolver.SyncObject</c> — then constructed
    /// the loader without them, so NONE of the row security the loader implements ever
    /// ran outside its own tests: a caller could submit another tenant's primary key
    /// and have that tenant's row loaded and diffed. Making them required is what stops
    /// an unsecured loader from being constructible at all.
    /// </summary>
    public TreeSyncStateLoader(
        ISqlDialect dialect,
        IDbModel model,
        IDictionary<string, object?> userContext,
        IServiceProvider? services = null,
        int maxDepth = 3)
    {
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _userContext = userContext ?? throw new ArgumentNullException(nameof(userContext));
        _services = services;
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

            // Descend into every one of the child table's own collections (bounded
            // by maxDepth). Freshly loaded child rows carry no nested collections
            // yet, so the set of grand-links to fetch cannot be derived from the
            // rows — it must be the child table's full MultiLinks set. The engine
            // only DIFFS the grand-links present in the submitted subtree (an
            // omitted collection is skipped, never orphan-deleted), but it needs the
            // loaded grandchildren to (a) UPDATE an existing depth-2 row instead of
            // re-INSERTing it into a PK violation, and (b) cascade-delete a deleted
            // child's grandchildren. An empty grand-link set made the recursion a
            // no-op past depth 1, causing both bugs.
            var grandLinks = new HashSet<string>(childTable.MultiLinks.Keys, StringComparer.OrdinalIgnoreCase);
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
        // The whole read chain for this table, in one place. InternalDiff because the
        // loaded rows are consumed ONLY by TreeSyncEngine's diff and never returned to
        // the caller (DbTableMutateResolver.SyncObject returns just the root key) —
        // see ReadProjection.InternalDiff for why masking here would be actively
        // wrong: it would make every encrypted field compare unequal to the submitted
        // plaintext and be rewritten on every sync.
        var chain = TableReadChain.For(
            _services, _model, table, _userContext, ReadProjection.InternalDiff,
            QueryType.Standard, table.GraphQlName, isNestedQuery: true);

        // Narrow the projection to the columns the caller may read. The loader used to
        // SELECT every column regardless of the column read guard.
        var columns = chain.ReadableColumns;
        if (columns.Count == 0)
            return new List<Dictionary<string, object?>>();

        var columnSql = string.Join(", ", columns.Select(c => _dialect.EscapeIdentifier(c.ColumnName)));
        var tableRef = _dialect.TableReference(table.TableSchema, table.DbName);

        // Apply this table's own tenant/soft-delete/policy filter to the read so
        // the diff never sees a row the caller couldn't otherwise read (and so a
        // caller can't use tree-sync to probe existence of another tenant's PK).
        var securityParams = new SqlParameterCollection();
        var securityWhere = GetSecurityFilterSql(chain, table, securityParams);
        var combinedWhere = string.IsNullOrEmpty(securityWhere)
            ? whereClause
            : $"{whereClause} AND ({securityWhere})";

        var sql = $"SELECT {columnSql} FROM {tableRef} WHERE {combinedWhere}";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        foreach (var securityParameter in securityParams.Parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = securityParameter.Name;
            p.Value = securityParameter.Value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        try
        {
            // Materialize THROUGH the chain, which is where the crypto projection
            // lives. A raw reader loop here is what left encrypted columns as stored
            // envelopes, so every submitted plaintext compared unequal to them and an
            // unchanged encrypted field was rewritten on every single sync.
            return await chain.ReadRowsAsync(cmd, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not BifrostExecutionError)
        {
            throw BifrostExecutionError.FromDatabaseException(ex);
        }
    }

    // Renders the combined tenant/soft-delete/policy filter for a table, or ""
    // when no transformer applies.
    private string GetSecurityFilterSql(
        TableReadChain chain, IDbTable table, SqlParameterCollection securityParams)
    {
        var securityFilter = chain.RowFilter;
        if (securityFilter == null)
            return "";

        var rendered = securityFilter.ToSqlParameterized(_model, _dialect, securityParams, alias: table.DbName);
        return rendered.Sql;
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
