namespace BifrostQL.Core.Model;

/// <summary>
/// Maps SQL Server data types to GraphQL types.
/// Handles SQL Server-specific types like uniqueidentifier, money, datetime2,
/// geography, geometry, xml, etc.
/// </summary>
public sealed class SqlServerTypeMapper : ITypeMapper
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly SqlServerTypeMapper Instance = new();

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "smallint", "tinyint", "bigint",
        "decimal", "numeric", "money", "smallmoney",
        "float", "real",
        "bit",
        "datetime", "datetime2", "datetimeoffset", "date", "time", "smalldatetime",
        "varchar", "nvarchar", "char", "nchar", "text", "ntext",
        "binary", "varbinary", "image",
        "uniqueidentifier",
        "xml", "json",
        "sql_variant", "timestamp", "rowversion",
        "geography", "geometry", "hierarchyid",
    };

    /// <inheritdoc />
    /// <remarks>
    /// Type mapping: int->Int, smallint->Short, tinyint->Byte, bigint->BigInt,
    /// decimal->Decimal, float/real->Float, bit->Boolean,
    /// datetime/datetime2/smalldatetime->DateTime, datetimeoffset->DateTimeOffset,
    /// json->JSON. All other types (varchar, nvarchar, uniqueidentifier, xml, etc.) map to String.
    /// </remarks>
    public string GetGraphQlType(string dataType)
    {
        return dataType.ToLowerInvariant().Trim() switch
        {
            "int" => "Int",
            "smallint" => "Short",
            "tinyint" => "Byte",
            "bigint" => "BigInt",
            "decimal" => "Decimal",
            "float" or "real" => "Float",
            "bit" => "Boolean",
            "datetime" or "datetime2" or "smalldatetime" => "DateTime",
            "datetimeoffset" => "DateTimeOffset",
            "json" => "JSON",
            _ => "String",
        };
    }

    /// <inheritdoc />
    public string GetGraphQlTypeName(string dataType, bool isNullable = false)
        => $"{GetGraphQlType(dataType)}{(isNullable ? "" : "!")}";

    /// <inheritdoc />
    /// <remarks>
    /// DateTime types (datetime, datetime2, datetimeoffset) are mapped to String for mutations
    /// to allow flexible date format input.
    /// </remarks>
    public string GetGraphQlInsertTypeName(string dataType, bool isNullable = false)
    {
        var normalized = dataType.ToLowerInvariant().Trim();
        if (normalized is "datetime2" or "datetime" or "datetimeoffset")
            return $"String{(isNullable ? "" : "!")}";

        return $"{GetGraphQlType(dataType)}{(isNullable ? "" : "!")}";
    }

    /// <inheritdoc />
    public string GetFilterInputTypeName(string dataType)
        => $"FilterType{GetGraphQlType(dataType)}Input";

    /// <inheritdoc />
    public bool IsSupported(string dataType)
        => KnownTypes.Contains(dataType.ToLowerInvariant().Trim());
}
