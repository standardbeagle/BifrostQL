using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules.Eav;

/// <summary>
/// Main entry point for EAV flattening functionality.
/// Orchestrates detection, schema transformation, and query execution.
/// </summary>
public sealed class EavModule
{
    private readonly IDbModel _model;
    private readonly ISqlDialect _dialect;
    private readonly ITypeMapper _typeMapper;
    private readonly EavSchemaCache _cache;
    private readonly Lazy<IReadOnlyList<EavFlattenedTable>> _flattenedTables;

    public EavModule(IDbModel model, ISqlDialect dialect, ITypeMapper? typeMapper = null, EavSchemaCache? cache = null)
    {
        _model = model;
        _dialect = dialect;
        _typeMapper = typeMapper ?? SqlServerTypeMapper.Instance;
        _cache = cache ?? new EavSchemaCache();
        _flattenedTables = new Lazy<IReadOnlyList<EavFlattenedTable>>(BuildFlattenedTables);
    }

    /// <summary>
    /// Gets all flattened EAV tables defined in the model.
    /// </summary>
    public IReadOnlyList<EavFlattenedTable> FlattenedTables => _flattenedTables.Value;

    /// <summary>
    /// Gets a flattened table by parent table name.
    /// </summary>
    public EavFlattenedTable? GetFlattenedTable(string parentTableDbName)
    {
        return FlattenedTables.FirstOrDefault(t =>
            string.Equals(t.ParentTable.DbName, parentTableDbName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets a flattened table by its meta table name.
    /// </summary>
    public EavFlattenedTable? GetFlattenedTableByMetaTable(string metaTableDbName)
    {
        return FlattenedTables.FirstOrDefault(t =>
            string.Equals(t.MetaTable.DbName, metaTableDbName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Discovers dynamic columns for a flattened table from the database.
    /// Uses caching to avoid repeated queries.
    /// </summary>
    public IReadOnlyList<EavColumn> DiscoverColumns(EavFlattenedTable table, DbConnection connection)
    {
        // Check cache first
        var cached = _cache.GetColumns(table.MetaTable.DbName);
        if (cached != null)
            return cached;

        // Query database for distinct meta_keys
        var discoverer = new EavColumnDiscoverer(_dialect);
        var discoverySql = discoverer.GenerateDiscoverySql(table.Config, table.MetaTable);

        var metaKeys = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = discoverySql.Sql;
            foreach (var param in discoverySql.Parameters)
            {
                var dbParam = command.CreateParameter();
                dbParam.ParameterName = param.Name;
                dbParam.Value = param.Value ?? DBNull.Value;
                command.Parameters.Add(dbParam);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetValue(0)?.ToString();
                if (!string.IsNullOrEmpty(key))
                    metaKeys.Add(key);
            }
        }

        var columns = discoverer.CreateColumns(metaKeys);
        _cache.SetColumns(table.MetaTable.DbName, columns);

        return columns;
    }

    /// <summary>
    /// Executes a query on a flattened EAV table.
    /// </summary>
    public EavQueryResult ExecuteQuery(
        EavFlattenedQuery query,
        DbConnection connection,
        IReadOnlyList<EavColumn>? columns = null)
    {
        columns ??= DiscoverColumns(query.FlattenedTable, connection);

        var transformer = new EavQueryTransformer(_dialect);

        // Build and execute main query
        var mainSql = transformer.GenerateFlattenedQuerySql(
            query.FlattenedTable, columns, query.Filter, query.Limit, query.Offset);

        var results = new List<Dictionary<string, object?>>();
        int? totalCount = null;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = mainSql.Sql;
            foreach (var param in mainSql.Parameters)
            {
                var dbParam = command.CreateParameter();
                dbParam.ParameterName = param.Name;
                dbParam.Value = param.Value ?? DBNull.Value;
                command.Parameters.Add(dbParam);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    var value = reader.GetValue(i);
                    row[name] = value == DBNull.Value ? null : value;
                }
                results.Add(row);
            }
        }

        // Get total count if requested
        if (query.IncludeTotal)
        {
            var countSql = transformer.GenerateCountSql(query.FlattenedTable, query.Filter);
            using var command = connection.CreateCommand();
            command.CommandText = countSql.Sql;
            foreach (var param in countSql.Parameters)
            {
                var dbParam = command.CreateParameter();
                dbParam.ParameterName = param.Name;
                dbParam.Value = param.Value ?? DBNull.Value;
                command.Parameters.Add(dbParam);
            }

            var countResult = command.ExecuteScalar();
            totalCount = countResult != null ? Convert.ToInt32(countResult) : 0;
        }

        return new EavQueryResult
        {
            Rows = results,
            TotalCount = totalCount,
            Columns = columns,
        };
    }

    /// <summary>
    /// Invalidates the column cache for a specific meta table.
    /// </summary>
    public void InvalidateCache(string metaTableDbName)
    {
        _cache.Invalidate(metaTableDbName);
    }

    /// <summary>
    /// Invalidates all cached column schemas.
    /// </summary>
    public void InvalidateAllCaches()
    {
        _cache.InvalidateAll();
    }

    private IReadOnlyList<EavFlattenedTable> BuildFlattenedTables()
    {
        var transformer = new EavSchemaTransformer(_model, _dialect, _typeMapper);
        return transformer.BuildFlattenedTables();
    }
}

/// <summary>
/// Result of executing a flattened EAV query.
/// </summary>
public sealed class EavQueryResult
{
    /// <summary>Query result rows</summary>
    public required IReadOnlyList<Dictionary<string, object?>> Rows { get; init; }

    /// <summary>Total row count (if requested)</summary>
    public int? TotalCount { get; init; }

    /// <summary>Columns in the result</summary>
    public required IReadOnlyList<EavColumn> Columns { get; init; }
}

/// <summary>
/// Extension methods for integrating EAV module with the query pipeline.
/// </summary>
public static class EavModuleExtensions
{
    /// <summary>
    /// Checks if a table is a flattened EAV virtual table.
    /// </summary>
    public static bool IsEavFlattenedTable(this IDbTable table, EavModule module)
    {
        return module.GetFlattenedTable(table.DbName) != null;
    }

    /// <summary>
    /// Gets the EAV configuration for a table if it exists.
    /// </summary>
    public static EavConfig? GetEavConfig(this IDbTable table, IDbModel model)
    {
        return model.EavConfigs.FirstOrDefault(e =>
            string.Equals(e.MetaTableDbName, table.DbName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if a table is an EAV meta table.
    /// </summary>
    public static bool IsEavMetaTable(this IDbTable table, IDbModel model)
    {
        return model.EavConfigs.Any(e =>
            string.Equals(e.MetaTableDbName, table.DbName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if a table is an EAV parent table.
    /// </summary>
    public static bool IsEavParentTable(this IDbTable table, IDbModel model)
    {
        return model.EavConfigs.Any(e =>
            string.Equals(e.ParentTableDbName, table.DbName, StringComparison.OrdinalIgnoreCase));
    }
}
