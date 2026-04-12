using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Schema;

namespace BifrostQL.Core.Modules.Eav;

/// <summary>
/// Transforms the database schema to include flattened EAV virtual tables.
/// Adds virtual tables and fields that expose EAV data as regular columns.
/// </summary>
public sealed class EavSchemaTransformer
{
    private readonly IDbModel _model;
    private readonly ISqlDialect _dialect;
    private readonly ITypeMapper _typeMapper;

    public EavSchemaTransformer(IDbModel model, ISqlDialect dialect, ITypeMapper? typeMapper = null)
    {
        _model = model;
        _dialect = dialect;
        _typeMapper = typeMapper ?? SqlServerTypeMapper.Instance;
    }

    /// <summary>
    /// Builds flattened EAV tables from the model's EAV configurations.
    /// </summary>
    public IReadOnlyList<EavFlattenedTable> BuildFlattenedTables()
    {
        var flattenedTables = new List<EavFlattenedTable>();

        foreach (var config in _model.EavConfigs)
        {
            var parentTable = _model.Tables.FirstOrDefault(t =>
                string.Equals(t.DbName, config.ParentTableDbName, StringComparison.OrdinalIgnoreCase));
            var metaTable = _model.Tables.FirstOrDefault(t =>
                string.Equals(t.DbName, config.MetaTableDbName, StringComparison.OrdinalIgnoreCase));

            if (parentTable == null || metaTable == null)
                continue;

            // Create a placeholder - columns will be discovered at query time
            // or from a cached schema
            var flattenedTable = new EavFlattenedTable
            {
                ParentTable = parentTable,
                MetaTable = metaTable,
                Config = config,
                DynamicColumns = Array.Empty<EavColumn>(),
            };

            flattenedTables.Add(flattenedTable);
        }

        return flattenedTables;
    }

    /// <summary>
    /// Generates GraphQL schema extensions for flattened EAV tables.
    /// This adds a "flattened" field to parent tables that exposes EAV data as columns.
    /// </summary>
    public string GenerateSchemaExtensions(IReadOnlyList<EavFlattenedTable> flattenedTables)
    {
        var builder = new StringBuilder();

        foreach (var table in flattenedTables)
        {
            // Add flattened type definition
            builder.AppendLine(GenerateFlattenedTypeDefinition(table));

            // Add field to parent table type
            builder.AppendLine(GenerateParentFieldExtension(table));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Generates a GraphQL type definition for a flattened EAV table.
    /// </summary>
    private string GenerateFlattenedTypeDefinition(EavFlattenedTable table)
    {
        var builder = new StringBuilder();
        var typeName = GetFlattenedTypeName(table.ParentTable);

        builder.AppendLine($"type {typeName} {{");

        // Add parent table's primary key columns
        foreach (var pk in table.ParentTable.KeyColumns)
        {
            var gqlType = SchemaGenerator.GetGraphQlTypeName(pk.EffectiveDataType, pk.IsNullable, _typeMapper);
            builder.AppendLine($"\t{pk.GraphQlName}: {gqlType}!");
        }

        // Add dynamic EAV columns
        foreach (var col in table.DynamicColumns)
        {
            var gqlType = SchemaGenerator.GetGraphQlTypeName(col.DataType, col.IsNullable, _typeMapper);
            builder.AppendLine($"\t{col.GraphQlName}: {gqlType}");
        }

        builder.AppendLine("}");

        return builder.ToString();
    }

    /// <summary>
    /// Generates a field extension for the parent table type.
    /// </summary>
    private string GenerateParentFieldExtension(EavFlattenedTable table)
    {
        var fieldName = GetFlattenedFieldName(table.MetaTable);
        var typeName = GetFlattenedTypeName(table.ParentTable);
        return $"extend type {table.ParentTable.GraphQlName} {{\n\t{fieldName}: {typeName}\n}}";
    }

    /// <summary>
    /// Generates a paged type definition for flattened EAV queries.
    /// </summary>
    public string GeneratePagedTypeDefinition(IDbTable parentTable)
    {
        var typeName = GetFlattenedTypeName(parentTable);
        return $@"type {typeName}_paged {{
	data: [{typeName}]
	total: Int!
	offset: Int
	limit: Int
}}";
    }

    /// <summary>
    /// Generates query field definition for the flattened table.
    /// </summary>
    public string GenerateQueryFieldDefinition(EavFlattenedTable table)
    {
        var fieldName = GetFlattenedQueryFieldName(table.ParentTable, table.MetaTable);
        var typeName = GetFlattenedTypeName(table.ParentTable);
        return $"{fieldName}(limit: Int, offset: Int): {typeName}_paged";
    }

    /// <summary>
    /// Gets the GraphQL type name for a flattened table.
    /// </summary>
    public static string GetFlattenedTypeName(IDbTable parentTable)
    {
        return $"{parentTable.GraphQlName}_flattened";
    }

    /// <summary>
    /// Gets the field name for the flattened data on the parent type.
    /// </summary>
    public static string GetFlattenedFieldName(IDbTable metaTable)
    {
        // e.g., wp_postmeta -> _flattened_postmeta
        var baseName = metaTable.DbName;
        var lastUnderscore = baseName.LastIndexOf('_');
        if (lastUnderscore > 0)
        {
            baseName = baseName[(lastUnderscore + 1)..];
        }
        return $"_flattened_{baseName}";
    }

    /// <summary>
    /// Gets the query field name for accessing flattened data at root level.
    /// </summary>
    public static string GetFlattenedQueryFieldName(IDbTable parentTable, IDbTable metaTable)
    {
        var fieldName = GetFlattenedFieldName(metaTable);
        return $"{parentTable.GraphQlName}{fieldName}";
    }
}

/// <summary>
/// Cache for EAV column discovery to avoid repeated database queries.
/// </summary>
public sealed class EavSchemaCache
{
    private readonly Dictionary<string, IReadOnlyList<EavColumn>> _cache = new();
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, DateTime> _expiry = new();

    public EavSchemaCache(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
    }

    public IReadOnlyList<EavColumn>? GetColumns(string metaTableName)
    {
        if (_cache.TryGetValue(metaTableName, out var columns))
        {
            if (_expiry.TryGetValue(metaTableName, out var expiry) && DateTime.UtcNow < expiry)
            {
                return columns;
            }
            // Expired - remove
            _cache.Remove(metaTableName);
            _expiry.Remove(metaTableName);
        }
        return null;
    }

    public void SetColumns(string metaTableName, IReadOnlyList<EavColumn> columns)
    {
        _cache[metaTableName] = columns;
        _expiry[metaTableName] = DateTime.UtcNow.Add(_ttl);
    }

    public void Invalidate(string metaTableName)
    {
        _cache.Remove(metaTableName);
        _expiry.Remove(metaTableName);
    }

    public void InvalidateAll()
    {
        _cache.Clear();
        _expiry.Clear();
    }
}
