using BifrostQL.Core.Model;
using BifrostQL.Core.Utils;

namespace BifrostQL.SqlServer;

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
        return StringNormalizer.NormalizeType(dataType) switch
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
        var normalized = StringNormalizer.NormalizeType(dataType);
        if (normalized is "datetime2" or "datetime" or "datetimeoffset")
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
    /// SQL Server LOBs: the deprecated text/ntext/image types, xml, and the
    /// (max)-length variants of (n)varchar/varbinary. Bounded (n)varchar(n) is not
    /// a large value.
    /// </remarks>
    public bool IsLargeValue(string dataType)
    {
        var normalized = StringNormalizer.NormalizeType(dataType);
        if (normalized.Contains("(max)")) return true;
        return normalized is "text" or "ntext" or "image" or "xml";
    }

    /// <inheritdoc />
    /// <remarks>T-SQL tinyint is unsigned (0..255); the interface default covers
    /// int/smallint/bigint, which SQL Server stores at their .NET-named widths.</remarks>
    public NumericValueRange? GetIntegerRange(string dataType)
        => StringNormalizer.NormalizeType(dataType) == "tinyint"
            ? new NumericValueRange(byte.MinValue, byte.MaxValue)
            : TypeMapperDefaults.IntegerRange(dataType);

    /// <inheritdoc />
    /// <remarks>
    /// datetime cannot store dates before 1753-01-01 and smalldatetime is bounded to
    /// 1900-01-01..2079-06-06 — both are classic insert-time failures worth refusing
    /// server-side. datetime2/date/datetimeoffset span 0001..9999 and need no bound.
    /// </remarks>
    public TemporalValueRange? GetTemporalRange(string dataType)
        => StringNormalizer.NormalizeType(dataType) switch
        {
            "datetime" => new TemporalValueRange(
                new DateTime(1753, 1, 1), new DateTime(9999, 12, 31, 23, 59, 59, 997)),
            "smalldatetime" => new TemporalValueRange(
                new DateTime(1900, 1, 1), new DateTime(2079, 6, 6)),
            _ => null,
        };
}
