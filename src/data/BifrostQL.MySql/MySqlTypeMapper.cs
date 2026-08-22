using BifrostQL.Core.Model;
using BifrostQL.Core.Utils;

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
        return StringNormalizer.NormalizeType(dataType) switch
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
        var normalized = StringNormalizer.NormalizeType(dataType);
        if (normalized is "datetime" or "timestamp")
            return $"String{(isNullable ? "" : "!")}";

        return $"{GetGraphQlType(dataType)}{(isNullable ? "" : "!")}";
    }

    /// <inheritdoc />
    public string GetFilterInputTypeName(string dataType)
        => $"FilterType{GetGraphQlType(dataType)}Input";

    /// <inheritdoc />
    public bool IsSupported(string dataType)
        => KnownTypes.Contains(StringNormalizer.NormalizeType(dataType));

    /// <inheritdoc />
    /// <remarks>
    /// MySQL's TEXT and BLOB families are true LOB types (stored off-page, no
    /// default, restricted indexing), so all of them count as large values.
    /// </remarks>
    public bool IsLargeValue(string dataType)
        => StringNormalizer.NormalizeType(dataType)
            is "tinytext" or "text" or "mediumtext" or "longtext"
            or "tinyblob" or "blob" or "mediumblob" or "longblob";

    /// <inheritdoc />
    /// <remarks>
    /// information_schema reports "int" for both signed INT and INT UNSIGNED — the
    /// signedness is not captured in the model — so each range is the UNION of the
    /// signed and unsigned ranges: nothing valid is refused, and everything outside
    /// the union would fail in the engine whichever signedness applies. tinyint is
    /// not bounded here because it maps to Boolean (MySqlConnector TINYINT(1)).
    /// </remarks>
    public NumericValueRange? GetIntegerRange(string dataType)
        => StringNormalizer.NormalizeType(dataType) switch
        {
            "int" or "integer" => new NumericValueRange(int.MinValue, uint.MaxValue),
            "smallint" => new NumericValueRange(short.MinValue, ushort.MaxValue),
            "mediumint" => new NumericValueRange(-8388608m, 16777215m),
            "bigint" => new NumericValueRange(long.MinValue, ulong.MaxValue),
            _ => null,
        };

    /// <inheritdoc />
    /// <remarks>
    /// DATETIME/DATE are bounded below at 1000-01-01 and TIMESTAMP is the classic
    /// 1970..2038 window (upper bound conservative: the engine's exact ceiling is
    /// 2038-01-19 03:14:07 UTC, session-timezone dependent, so refuse only what is
    /// out of range in EVERY timezone and let the engine arbitrate the sliver).
    /// </remarks>
    public TemporalValueRange? GetTemporalRange(string dataType)
        => StringNormalizer.NormalizeType(dataType) switch
        {
            "datetime" or "date" => new TemporalValueRange(
                new DateTime(1000, 1, 1), new DateTime(9999, 12, 31, 23, 59, 59)),
            "timestamp" => new TemporalValueRange(
                new DateTime(1970, 1, 2), new DateTime(2038, 1, 18)),
            _ => null,
        };
}
