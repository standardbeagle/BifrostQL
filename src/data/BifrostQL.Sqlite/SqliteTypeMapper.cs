using BifrostQL.Core.Model;
using BifrostQL.Core.Utils;

namespace BifrostQL.Sqlite;

/// <summary>
/// Maps SQLite data types to GraphQL types.
/// SQLite uses dynamic typing with type affinity rules. Column types are
/// advisory, and actual storage depends on the value. This mapper handles
/// common declared types and SQLite's type affinity categories.
/// </summary>
public sealed class SqliteTypeMapper : ITypeMapper
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly SqliteTypeMapper Instance = new();

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "integer", "int", "tinyint", "smallint", "mediumint", "bigint",
        "real", "double", "float", "numeric", "decimal",
        "text", "varchar", "char", "clob", "nvarchar", "nchar",
        "blob", "none", "any",
        "boolean", "bit",
        "date", "datetime", "timestamp",
        "json",
    };

    /// <inheritdoc />
    /// <remarks>
    /// Type mapping follows SQLite type affinity rules. INTEGER PRIMARY KEY is the rowid alias.
    /// integer/int/mediumint->Int, smallint->Short, tinyint->Byte, bigint->BigInt,
    /// real/double/float->Float, numeric/decimal->Decimal, boolean/bit->Boolean,
    /// datetime/timestamp->DateTime, json->JSON. All other types map to String.
    /// </remarks>
    public string GetGraphQlType(string dataType)
    {
        var normalized = StripPrecision(dataType);

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
        var normalized = StripPrecision(dataType);
        if (normalized is "datetime" or "timestamp")
            return $"String{(isNullable ? "" : "!")}";

        return $"{GetGraphQlType(dataType)}{(isNullable ? "" : "!")}";
    }

    /// <inheritdoc />
    public string GetFilterInputTypeName(string dataType)
        => $"FilterType{GetGraphQlType(dataType)}Input";

    /// <inheritdoc />
    public bool IsSupported(string dataType)
        => KnownTypes.Contains(StripPrecision(dataType));

    /// <inheritdoc />
    /// <remarks>
    /// SQLite has type affinity, not real types: TEXT is the ONLY string type and
    /// idiomatic schemas declare every string (and often every date) as TEXT, so it
    /// must never count as a large value. Only BLOB affinity carries binary
    /// fetch-on-demand semantics.
    /// </remarks>
    public bool IsLargeValue(string dataType)
        => StripPrecision(dataType) is "blob";

    /// <inheritdoc />
    /// <remarks>
    /// SQLite type affinity stores every integer as a 64-bit value whatever the
    /// declared name — a column declared "int" happily holds long.MaxValue — so
    /// asserting the declared name's range would refuse values the engine accepts.
    /// Only the storage class itself (64-bit) bounds, matching the default bigint
    /// range, and nothing narrower is enforced.
    /// </remarks>
    public NumericValueRange? GetIntegerRange(string dataType)
        => StripPrecision(dataType) is "int" or "integer" or "smallint" or "tinyint" or "bigint"
            or "int2" or "int4" or "int8" or "mediumint"
            ? new NumericValueRange(long.MinValue, long.MaxValue)
            : null;

    private static string StripPrecision(string dataType)
    {
        var normalized = StringNormalizer.NormalizeType(dataType);
        var parenIdx = normalized.IndexOf('(');
        return parenIdx >= 0 ? normalized.Substring(0, parenIdx).TrimEnd() : normalized;
    }
}
