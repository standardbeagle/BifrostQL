using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Model;

/// <summary>
/// Provider-neutral default type mapper for standard ANSI SQL data types.
/// Used as the Core fallback when no dialect-specific <see cref="ITypeMapper"/>
/// has been supplied (e.g., a model built without a connection factory).
/// Dialect packages (SQL Server, PostgreSQL, MySQL, SQLite) provide their own
/// mappers to handle provider-specific types.
/// </summary>
public sealed class AnsiSqlTypeMapper : ITypeMapper
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly AnsiSqlTypeMapper Instance = new();

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "integer", "smallint", "tinyint", "bigint",
        "decimal", "numeric", "float", "real", "double",
        "bit", "boolean",
        "datetime", "datetime2", "datetimeoffset", "date", "time", "timestamp",
        "varchar", "nvarchar", "char", "nchar", "text",
        "binary", "varbinary", "blob",
        "json",
    };

    /// <inheritdoc />
    /// <remarks>
    /// Standard mapping: int/integer->Int, smallint->Short, tinyint->Byte, bigint->BigInt,
    /// decimal/numeric->Decimal, float/real/double->Float, bit/boolean->Boolean,
    /// datetime variants->DateTime, datetimeoffset->DateTimeOffset, json->JSON.
    /// All other types map to String.
    /// </remarks>
    public string GetGraphQlType(string dataType)
    {
        return StringNormalizer.NormalizeType(dataType) switch
        {
            "int" or "integer" => "Int",
            "smallint" => "Short",
            "tinyint" => "Byte",
            "bigint" => "BigInt",
            "decimal" or "numeric" => "Decimal",
            "float" or "real" or "double" => "Float",
            "bit" or "boolean" => "Boolean",
            "datetime" or "datetime2" or "smalldatetime" or "timestamp" => "DateTime",
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
    /// DateTime types are mapped to String for mutations to allow flexible date format input.
    /// </remarks>
    public string GetGraphQlInsertTypeName(string dataType, bool isNullable = false)
    {
        var normalized = StringNormalizer.NormalizeType(dataType);
        if (normalized is "datetime2" or "datetime" or "datetimeoffset" or "timestamp")
            return $"String{(isNullable ? "" : "!")}";

        return $"{GetGraphQlType(dataType)}{(isNullable ? "" : "!")}";
    }

    /// <inheritdoc />
    public string GetFilterInputTypeName(string dataType)
        => $"FilterType{GetGraphQlType(dataType)}Input";

    /// <inheritdoc />
    public bool IsSupported(string dataType)
        => KnownTypes.Contains(StringNormalizer.NormalizeType(dataType));
}
