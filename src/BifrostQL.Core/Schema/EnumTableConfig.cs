using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema;

/// <summary>
/// Configuration for designating a lookup table as a GraphQL enum type.
/// Values are loaded at schema build time from the database.
///
/// Configuration via table metadata:
///   "dbo.status { enum: true }"              - Auto-detect: uses first non-PK string column as value
///   "dbo.status { enum: code }"              - Explicit value column
///   "dbo.status { enum: code:display_name }"  - Explicit value and label columns
/// </summary>
public sealed class EnumTableConfig
{
    public const string MetadataKey = "enum";

    /// <summary>The database table name.</summary>
    public string TableName { get; init; } = null!;

    /// <summary>The column containing enum values. When null, auto-detection is used.</summary>
    public string? ValueColumn { get; init; }

    /// <summary>Optional column containing human-readable labels.</summary>
    public string? LabelColumn { get; init; }

    /// <summary>The GraphQL enum type name derived from the table.</summary>
    public string GraphQlEnumName { get; init; } = null!;

    /// <summary>
    /// Attempts to parse enum configuration from a table's metadata.
    /// Returns null if the table has no enum metadata.
    /// </summary>
    public static EnumTableConfig? FromTable(IDbTable table)
    {
        if (!table.Metadata.TryGetValue(MetadataKey, out var metaValue) || metaValue == null)
            return null;

        var raw = metaValue.ToString()!.Trim();
        if (string.IsNullOrEmpty(raw))
            return null;

        string? valueColumn = null;
        string? labelColumn = null;

        if (!string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Split(':');
            valueColumn = parts[0].Trim();
            if (parts.Length > 1)
                labelColumn = parts[1].Trim();
        }

        return new EnumTableConfig
        {
            TableName = table.DbName,
            ValueColumn = string.IsNullOrEmpty(valueColumn) ? null : valueColumn,
            LabelColumn = string.IsNullOrEmpty(labelColumn) ? null : labelColumn,
            GraphQlEnumName = $"{table.GraphQlName}Values",
        };
    }

    /// <summary>
    /// Resolves the effective value column name, using auto-detection if not explicitly configured.
    /// Returns null if no suitable column can be found.
    /// </summary>
    public string? ResolveValueColumn(IDbTable table)
    {
        if (ValueColumn != null)
            return table.ColumnLookup.ContainsKey(ValueColumn) ? ValueColumn : null;

        // Auto-detect: first non-PK string-typed column
        var stringTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "varchar", "nvarchar", "char", "nchar", "text", "ntext"
        };

        var candidate = table.Columns
            .Where(c => !c.IsPrimaryKey)
            .FirstOrDefault(c => stringTypes.Contains(c.DataType));

        return candidate?.ColumnName;
    }
}
