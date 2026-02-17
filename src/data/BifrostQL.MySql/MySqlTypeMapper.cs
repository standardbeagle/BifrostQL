using BifrostQL.Core.Model;

namespace BifrostQL.MySql;

/// <summary>
/// Maps MySQL/MariaDB data types to GraphQL types.
/// Handles MySQL-specific types like enum, set, mediumint, mediumtext, etc.
/// </summary>
public sealed class MySqlTypeMapper : ITypeMapper
{
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

    public string GetGraphQlType(string dataType)
    {
        return dataType.ToLowerInvariant().Trim() switch
        {
            "int" or "integer" or "mediumint" => "Int",
            "smallint" => "Short",
            "tinyint" => "Byte",
            "bigint" => "BigInt",
            "decimal" or "numeric" => "Decimal",
            "float" or "double" or "real" => "Float",
            "bit" or "boolean" or "bool" => "Boolean",
            "datetime" or "timestamp" => "DateTime",
            "json" => "JSON",
            _ => "String",
        };
    }

    public string GetGraphQlTypeName(string dataType, bool isNullable = false)
        => $"{GetGraphQlType(dataType)}{(isNullable ? "" : "!")}";

    public string GetGraphQlInsertTypeName(string dataType, bool isNullable = false)
    {
        var normalized = dataType.ToLowerInvariant().Trim();
        if (normalized is "datetime" or "timestamp")
            return $"String{(isNullable ? "" : "!")}";

        return $"{GetGraphQlType(dataType)}{(isNullable ? "" : "!")}";
    }

    public string GetFilterInputTypeName(string dataType)
        => $"FilterType{GetGraphQlType(dataType)}Input";

    public bool IsSupported(string dataType)
        => KnownTypes.Contains(dataType.ToLowerInvariant().Trim());
}
