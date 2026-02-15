namespace BifrostQL.Core.Model;

/// <summary>
/// Detects lookup/reference tables based on structural heuristics and naming patterns.
/// A lookup table is a small, simple table (2-6 columns, single PK, no outbound FKs)
/// that is referenced by other tables for constrained value selection.
/// </summary>
public static class LookupTableDetector
{
    private static readonly HashSet<string> LookupNamePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "statuses", "type", "types", "category", "categories",
        "priority", "priorities", "kind", "kinds", "role", "roles",
        "state", "states", "country", "countries", "region", "regions",
        "currency", "currencies", "language", "languages",
    };

    private static readonly HashSet<string> ComplexDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "varbinary", "binary", "image", "blob",
        "xml", "json", "geography", "geometry",
    };

    private static readonly HashSet<string> StringDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "varchar", "nvarchar", "char", "nchar", "text", "ntext",
    };

    /// <summary>
    /// Determines whether a table is a lookup table based on structural heuristics.
    /// </summary>
    public static bool IsLookupTable(IDbTable table)
    {
        var columnCount = table.Columns.Count();
        if (columnCount < 2 || columnCount > 6)
            return false;

        if (table.KeyColumns.Count() != 1)
            return false;

        if (table.SingleLinks.Count > 0)
            return false;

        if (!AllColumnsAreSimpleTypes(table))
            return false;

        if (MatchesLookupPattern(table.DbName))
            return true;

        return false;
    }

    /// <summary>
    /// Detects column roles within a lookup table based on naming conventions.
    /// </summary>
    public static LookupColumnRoles DetectColumnRoles(IDbTable table)
    {
        var roles = new LookupColumnRoles
        {
            IdColumn = table.KeyColumns.First().ColumnName
        };

        foreach (var column in table.Columns)
        {
            if (column.IsPrimaryKey)
                continue;

            var name = column.ColumnName.ToLowerInvariant();

            if (roles.ValueColumn == null && IsValueColumn(name, column.DataType))
            {
                roles.ValueColumn = column.ColumnName;
                continue;
            }

            if (roles.LabelColumn == null && IsLabelColumn(name))
            {
                roles.LabelColumn = column.ColumnName;
                continue;
            }

            if (roles.SortColumn == null && IsSortColumn(name))
            {
                roles.SortColumn = column.ColumnName;
                continue;
            }

            if (roles.DescriptionColumn == null && IsDescriptionColumn(name))
            {
                roles.DescriptionColumn = column.ColumnName;
                continue;
            }

            if (roles.IconColumn == null && IsIconColumn(name))
            {
                roles.IconColumn = column.ColumnName;
                continue;
            }

            if (roles.ColorColumn == null && IsColorColumn(name))
            {
                roles.ColorColumn = column.ColumnName;
                continue;
            }
        }

        if (roles.LabelColumn == null)
        {
            var firstString = table.Columns
                .FirstOrDefault(c => !c.IsPrimaryKey && IsStringType(c.DataType));
            roles.LabelColumn = firstString?.ColumnName;
        }

        return roles;
    }

    /// <summary>
    /// Determines the UI mode for a lookup table based on its row count and thresholds.
    /// </summary>
    public static LookupUiMode SelectUiMode(int rowCount, int dropdownThreshold = 50, int autocompleteThreshold = 500)
    {
        if (rowCount <= dropdownThreshold)
            return LookupUiMode.Dropdown;
        if (rowCount <= autocompleteThreshold)
            return LookupUiMode.Autocomplete;
        return LookupUiMode.AsyncSearch;
    }

    private static bool AllColumnsAreSimpleTypes(IDbTable table)
    {
        foreach (var column in table.Columns)
        {
            if (ComplexDataTypes.Contains(column.DataType))
                return false;
        }
        return true;
    }

    private static bool MatchesLookupPattern(string tableName)
    {
        var lower = tableName.ToLowerInvariant();

        if (LookupNamePatterns.Contains(lower))
            return true;

        foreach (var suffix in LookupNamePatterns)
        {
            if (lower.EndsWith("_" + suffix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsStringType(string dataType) => StringDataTypes.Contains(dataType);

    private static bool IsValueColumn(string name, string dataType) =>
        (name is "code" or "slug" or "key" or "value") && IsStringType(dataType);

    private static bool IsLabelColumn(string name) =>
        name is "name" or "title" or "label" or "display_name";

    private static bool IsSortColumn(string name) =>
        name.EndsWith("_order", StringComparison.Ordinal) ||
        name.EndsWith("_sort", StringComparison.Ordinal) ||
        name is "position" or "sequence" or "sort" or "order" or "sort_order" or "display_order";

    private static bool IsDescriptionColumn(string name) =>
        name is "description" or "desc" or "note" or "help_text";

    private static bool IsIconColumn(string name) =>
        name is "icon" || name.StartsWith("icon_", StringComparison.Ordinal) || name.EndsWith("_icon", StringComparison.Ordinal);

    private static bool IsColorColumn(string name) =>
        name is "color" or "hex_color" || name.EndsWith("_color", StringComparison.Ordinal);
}

/// <summary>
/// Detected column roles within a lookup table.
/// </summary>
public sealed class LookupColumnRoles
{
    public string IdColumn { get; set; } = null!;
    public string? ValueColumn { get; set; }
    public string? LabelColumn { get; set; }
    public string? SortColumn { get; set; }
    public string? DescriptionColumn { get; set; }
    public string? IconColumn { get; set; }
    public string? ColorColumn { get; set; }

    /// <summary>
    /// Returns true if the lookup has rich display data beyond label.
    /// </summary>
    public bool HasRichData => IconColumn != null || ColorColumn != null || DescriptionColumn != null;
}

/// <summary>
/// UI control mode for lookup table foreign keys.
/// </summary>
public enum LookupUiMode
{
    /// <summary>Standard dropdown (select element). Best for 0-50 rows.</summary>
    Dropdown,

    /// <summary>Searchable select with client-side filtering. Best for 51-500 rows.</summary>
    Autocomplete,

    /// <summary>Async search input with server-side filtering. Best for 500+ rows.</summary>
    AsyncSearch,
}

/// <summary>
/// Configuration for a detected lookup table, combining column roles and UI preferences.
/// </summary>
public sealed class LookupTableConfig
{
    public string TableName { get; }
    public string Schema { get; }
    public LookupColumnRoles Roles { get; }
    public int DropdownThreshold { get; }
    public int AutocompleteThreshold { get; }

    public LookupTableConfig(string schema, string tableName, LookupColumnRoles roles,
        int dropdownThreshold = 50, int autocompleteThreshold = 500)
    {
        Schema = schema;
        TableName = tableName;
        Roles = roles;
        DropdownThreshold = dropdownThreshold;
        AutocompleteThreshold = autocompleteThreshold;
    }

    /// <summary>
    /// Creates a config from automatic detection of a table.
    /// </summary>
    public static LookupTableConfig FromDetection(IDbTable table)
    {
        return new LookupTableConfig(
            table.TableSchema,
            table.DbName,
            LookupTableDetector.DetectColumnRoles(table));
    }

    /// <summary>
    /// Creates a config with explicit metadata overrides for column roles and thresholds.
    /// </summary>
    public static LookupTableConfig FromMetadata(IDbTable table,
        string? labelColumn = null, string? valueColumn = null, string? sortColumn = null,
        int dropdownThreshold = 50, int autocompleteThreshold = 500)
    {
        var roles = LookupTableDetector.DetectColumnRoles(table);

        if (labelColumn != null) roles.LabelColumn = labelColumn;
        if (valueColumn != null) roles.ValueColumn = valueColumn;
        if (sortColumn != null) roles.SortColumn = sortColumn;

        return new LookupTableConfig(
            table.TableSchema, table.DbName, roles,
            dropdownThreshold, autocompleteThreshold);
    }
}
