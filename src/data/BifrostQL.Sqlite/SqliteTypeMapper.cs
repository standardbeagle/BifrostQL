using BifrostQL.Core.Model;

namespace BifrostQL.Sqlite;

/// <summary>
/// Maps SQLite data types to GraphQL types.
/// SQLite uses dynamic typing with type affinity rules. Column types are
/// advisory, and actual storage depends on the value. This mapper handles
/// common declared types and SQLite's type affinity categories.
/// </summary>
public sealed class SqliteTypeMapper : ITypeMapper
{
    public static readonly SqliteTypeMapper Instance = new();

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "integer", "int", "tinyint", "smallint", "mediumint", "bigint",
        "real", "double", "float", "numeric", "decimal",
        "text", "varchar", "char", "clob", "nvarchar", "nchar",
        "blob", "none",
        "boolean", "bit",
        "date", "datetime", "timestamp",
        "json",
    };

    public string GetGraphQlType(string dataType)
    {
        var normalized = dataType.ToLowerInvariant().Trim();

        // SQLite INTEGER PRIMARY KEY is the rowid alias and always an integer.
        // Other integer types follow SQLite affinity rules.
        return normalized switch
        {
            "integer" or "int" or "mediumint" => "Int",
            "smallint" => "Short",
            "tinyint" => "Byte",
            "bigint" => "BigInt",
            "real" or "double" or "float" => "Float",
            "numeric" or "decimal" => "Decimal",
            "boolean" or "bit" => "Boolean",
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
