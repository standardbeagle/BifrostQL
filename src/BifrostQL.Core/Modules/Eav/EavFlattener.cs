using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules.Eav;

/// <summary>
/// Detects EAV (Entity-Attribute-Value) patterns in database tables.
/// EAV tables typically have: entity_id, attribute_name, value columns.
/// Common examples: wp_postmeta (post_id, meta_key, meta_value), wp_usermeta
/// </summary>
public sealed class EavDetector
{
    /// <summary>
    /// Detects EAV pattern from table metadata.
    /// Returns EavConfig if the table has eav-parent, eav-fk, eav-key, eav-value metadata.
    /// </summary>
    public static EavConfig? DetectFromMetadata(IDbTable table, IReadOnlyCollection<IDbTable> allTables)
    {
        var parent = table.GetMetadataValue(MetadataKeys.Eav.Parent);
        var fk = table.GetMetadataValue(MetadataKeys.Eav.ForeignKey);
        var key = table.GetMetadataValue(MetadataKeys.Eav.Key);
        var value = table.GetMetadataValue(MetadataKeys.Eav.Value);

        if (string.IsNullOrWhiteSpace(parent) ||
            string.IsNullOrWhiteSpace(fk) ||
            string.IsNullOrWhiteSpace(key) ||
            string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Resolve parent table name
        var tablesByDbName = new HashSet<string>(
            allTables.Select(t => t.DbName), StringComparer.OrdinalIgnoreCase);

        var parentDbName = tablesByDbName.Contains(parent) ? parent : null;
        if (parentDbName == null)
        {
            // Try prefix-aware resolution
            var metaName = table.DbName;
            var lastUnderscore = metaName.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                var prefix = metaName[..lastUnderscore];
                while (prefix.Length > 0)
                {
                    var candidate = prefix + "_" + parent;
                    if (tablesByDbName.Contains(candidate))
                    {
                        parentDbName = candidate;
                        break;
                    }
                    var idx = prefix.LastIndexOf('_');
                    if (idx <= 0) break;
                    prefix = prefix[..idx];
                }
            }
        }

        if (parentDbName == null)
            return null;

        return new EavConfig(table.DbName, parentDbName, fk, key, value);
    }

    /// <summary>
    /// Heuristic detection of EAV pattern based on column names.
    /// Looks for common patterns like (entity_id, meta_key, meta_value) or similar.
    /// </summary>
    public static EavConfig? DetectHeuristic(IDbTable table, IReadOnlyCollection<IDbTable> allTables)
    {
        var columns = table.Columns.Select(c => c.ColumnName.ToLowerInvariant()).ToList();

        // Common EAV column patterns
        var keyPatterns = new[] { "meta_key", "attribute", "attr_name", "key", "name" };
        var valuePatterns = new[] { "meta_value", "value", "attr_value", "data" };
        var entityPatterns = new[] { "post_id", "user_id", "entity_id", "object_id", "parent_id", "id" };

        string? keyCol = null;
        string? valueCol = null;
        string? entityCol = null;

        // Find key column
        foreach (var pattern in keyPatterns)
        {
            keyCol = columns.FirstOrDefault(c => c == pattern || c.EndsWith("_" + pattern));
            if (keyCol != null) break;
        }

        // Find value column
        foreach (var pattern in valuePatterns)
        {
            valueCol = columns.FirstOrDefault(c => c == pattern || c.EndsWith("_" + pattern));
            if (valueCol != null) break;
        }

        // Find entity column (foreign key to parent)
        foreach (var pattern in entityPatterns)
        {
            entityCol = columns.FirstOrDefault(c => c == pattern || c.EndsWith("_" + pattern));
            if (entityCol != null) break;
        }

        if (keyCol == null || valueCol == null || entityCol == null)
            return null;

        // Try to find parent table based on entity column name
        var parentTable = FindParentTable(table, entityCol, allTables);
        if (parentTable == null)
            return null;

        return new EavConfig(table.DbName, parentTable.DbName, entityCol, keyCol, valueCol);
    }

    private static IDbTable? FindParentTable(IDbTable metaTable, string entityColumn, IReadOnlyCollection<IDbTable> allTables)
    {
        // Common patterns: post_id -> posts, user_id -> users
        var entityLower = entityColumn.ToLowerInvariant();

        // Remove common suffixes
        var baseName = entityLower
            .Replace("_id", "")
            .Replace("id", "");

        if (string.IsNullOrEmpty(baseName))
            baseName = entityLower;

        // Try exact match first
        var candidates = new List<string> { baseName, baseName + "s" };

        // Also try with prefix from meta table
        var metaName = metaTable.DbName;
        var lastUnderscore = metaName.LastIndexOf('_');
        if (lastUnderscore > 0)
        {
            var prefix = metaName[..lastUnderscore];
            candidates.Add(prefix + "_" + baseName);
            candidates.Add(prefix + "_" + baseName + "s");
        }

        foreach (var candidate in candidates)
        {
            var parent = allTables.FirstOrDefault(t =>
                string.Equals(t.DbName, candidate, StringComparison.OrdinalIgnoreCase));
            if (parent != null)
                return parent;
        }

        return null;
    }
}

/// <summary>
/// Represents a flattened EAV table with dynamic columns discovered from meta_keys.
/// </summary>
public sealed class EavFlattenedTable
{
    /// <summary>The parent entity table (e.g., wp_posts)</summary>
    public required IDbTable ParentTable { get; init; }

    /// <summary>The original EAV meta table (e.g., wp_postmeta)</summary>
    public required IDbTable MetaTable { get; init; }

    /// <summary>The EAV configuration</summary>
    public required EavConfig Config { get; init; }

    /// <summary>Dynamic columns discovered from meta_keys</summary>
    public required IReadOnlyList<EavColumn> DynamicColumns { get; init; }

    /// <summary>GraphQL name for the flattened virtual table</summary>
    public string GraphQlName => $"{ParentTable.GraphQlName}_flattened";
}

/// <summary>
/// Represents a dynamic column in a flattened EAV table.
/// </summary>
public sealed class EavColumn
{
    /// <summary>The meta_key value that defines this column</summary>
    public required string MetaKey { get; init; }

    /// <summary>The GraphQL-safe column name</summary>
    public required string GraphQlName { get; init; }

    /// <summary>The SQL column alias</summary>
    public required string SqlAlias { get; init; }

    /// <summary>Inferred data type (defaults to String for EAV values)</summary>
    public required string DataType { get; init; }

    /// <summary>Whether the column is nullable</summary>
    public bool IsNullable => true;

    /// <summary>Creates a ColumnDto representation for schema generation</summary>
    public ColumnDto ToColumnDto()
    {
        return new ColumnDto
        {
            ColumnName = SqlAlias,
            GraphQlName = GraphQlName,
            NormalizedName = GraphQlName,
            DataType = DataType,
            IsNullable = true,
            IsPrimaryKey = false,
        };
    }
}

/// <summary>
/// Discovers dynamic columns from an EAV meta table by querying distinct meta_keys.
/// </summary>
public sealed class EavColumnDiscoverer
{
    private readonly ISqlDialect _dialect;

    public EavColumnDiscoverer(ISqlDialect dialect)
    {
        _dialect = dialect;
    }

    /// <summary>
    /// Generates SQL to discover distinct meta_keys from the EAV table.
    /// </summary>
    public ParameterizedSql GenerateDiscoverySql(EavConfig config, IDbTable metaTable)
    {
        var keyCol = _dialect.EscapeIdentifier(config.KeyColumn);
        var tableRef = _dialect.TableReference(metaTable.TableSchema, metaTable.DbName);

        var sql = $"SELECT DISTINCT {keyCol} FROM {tableRef} WHERE {keyCol} IS NOT NULL ORDER BY {keyCol}";

        return new ParameterizedSql(sql, new List<SqlParameterInfo>());
    }

    /// <summary>
    /// Creates EavColumn definitions from discovered meta_keys.
    /// </summary>
    public IReadOnlyList<EavColumn> CreateColumns(IEnumerable<string> metaKeys)
    {
        var columns = new List<EavColumn>();

        foreach (var key in metaKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var graphQlName = ToGraphQlName(key);
            var sqlAlias = $"eav_{SanitizeSqlAlias(key)}";

            columns.Add(new EavColumn
            {
                MetaKey = key,
                GraphQlName = graphQlName,
                SqlAlias = sqlAlias,
                DataType = "nvarchar", // EAV values are typically stored as strings
            });
        }

        return columns;
    }

    private static string ToGraphQlName(string key)
    {
        // Convert meta_key to valid GraphQL identifier
        // e.g., "post_title" -> "post_title", "custom-field" -> "custom_field"
        var sanitized = key
            .Replace("-", "_")
            .Replace(".", "_")
            .Replace(" ", "_")
            .Replace("/", "_")
            .Replace("\\", "_");

        // Ensure it starts with a letter or underscore
        if (!string.IsNullOrEmpty(sanitized) && char.IsDigit(sanitized[0]))
            sanitized = "_" + sanitized;

        return sanitized;
    }

    private static string SanitizeSqlAlias(string key)
    {
        // Create a SQL-safe alias
        return key
            .Replace("-", "_")
            .Replace(".", "_")
            .Replace(" ", "_")
            .Replace("/", "_")
            .Replace("\\", "_")
            .Replace("[", "")
            .Replace("]", "");
    }
}
