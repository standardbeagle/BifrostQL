using BifrostQL.Core.Model;

namespace BifrostQL.MySql;

/// <summary>
/// Maps MySQL/MariaDB data types to GraphQL types.
/// Handles MySQL-specific types like enum, set, mediumint, mediumtext, etc.
/// </summary>
public sealed class MySqlTypeMapper : ITypeMapper
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly MySqlTypeMapper Instance = new();

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "integer", "smallint", "tinyint", "mediumint", "bigint",
        "decimal", "numeric", "float", "double", "real",
        "bit", "boolean", "bool",
        "datetime", "timestamp", "date", "time", "year",
        "char", "varchar", "tinytext", "text", "mediumtext", "longtext",
        "binary", "varbinary", "tinyblob", "blob", "mediumblob", "longblob",
        "enum", "set",
        "json",
        "geometry", "point", "linestring", "polygon",
        "multipoint", "multilinestring", "multipolygon", "geometrycollection",
    };

    /// <inheritdoc />
    /// <remarks>
    /// Type mapping: int/integer/mediumint->Int, smallint->Short,
    /// tinyint/bit/boolean/bool->Boolean (MySqlConnector returns .NET Boolean for TINYINT(1) by default),
    /// bigint->BigInt, decimal/numeric->Decimal, float/double/real->Float,
    /// datetime/timestamp->DateTime, json->JSON.
    /// All other types (varchar, text, enum, set, blob, etc.) map to String.
    /// </remarks>
    public string GetGraphQlType(string dataType)
    {
        return dataType.ToLowerInvariant().Trim() switch
        {
            "int" or "integer" or "mediumint" => "Int",
            "smallint" => "Short",
            "tinyint" or "bit" or "boolean" or "bool" => "Boolean",
            "bigint" => "BigInt",
            "decimal" or "numeric" => "Decimal",
            "float" or "double" or "real" => "Float",
            "datetime" or "timestamp" => "DateTime",
            "json" => "JSON",
            _ => "String",
        };
    }

    /// <inheritdoc />
    public string GetGraphQlTypeName(string dataType, bool isNullable = false)
        => $"{GetGraphQlType(dataType)}{(isNullable ? "" : "!")}";

    /// <inheritdoc />
    /// <remarks>
    /// DateTime types (datetime, timestamp) are mapped to String for mutations
    /// to allow flexible date format input.
    /// </remarks>
    public string GetGraphQlInsertTypeName(string dataType, bool isNullable = false)
    {
        var normalized = dataType.ToLowerInvariant().Trim();
        if (normalized is "datetime" or "timestamp")
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
