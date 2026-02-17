using BifrostQL.Core.Model;

namespace BifrostQL.Ngsql;

/// <summary>
/// Maps PostgreSQL data types to GraphQL types.
/// Handles PostgreSQL-specific types like jsonb, uuid, serial, arrays, interval, etc.
/// </summary>
public sealed class PostgresTypeMapper : ITypeMapper
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly PostgresTypeMapper Instance = new();

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "integer", "int", "int4", "smallint", "int2", "bigint", "int8",
        "serial", "smallserial", "bigserial",
        "decimal", "numeric", "money",
        "real", "float4", "double precision", "float8",
        "boolean", "bool",
        "timestamp without time zone", "timestamp with time zone",
        "timestamp", "timestamptz",
        "date", "time", "time without time zone", "time with time zone", "timetz",
        "interval",
        "character varying", "varchar", "character", "char", "text",
        "bytea",
        "uuid",
        "json", "jsonb",
        "xml",
        "point", "line", "lseg", "box", "path", "polygon", "circle",
        "cidr", "inet", "macaddr", "macaddr8",
        "bit", "bit varying",
        "tsvector", "tsquery",
        "oid",
        "array", "user-defined",
    };

    /// <inheritdoc />
    /// <remarks>
    /// Type mapping: integer/int/int4/serial->Int, smallint/int2/smallserial->Short,
    /// bigint/int8/bigserial->BigInt, decimal/numeric->Decimal,
    /// real/float4/double precision/float8->Float, boolean/bool->Boolean,
    /// timestamp (without tz)->DateTime, timestamptz->DateTimeOffset,
    /// json/jsonb->JSON. All other types (text, varchar, uuid, inet, etc.) map to String.
    /// </remarks>
    public string GetGraphQlType(string dataType)
    {
        return dataType.ToLowerInvariant().Trim() switch
        {
            "integer" or "int" or "int4" or "serial" => "Int",
            "smallint" or "int2" or "smallserial" => "Short",
            "bigint" or "int8" or "bigserial" => "BigInt",
            "decimal" or "numeric" => "Decimal",
            "real" or "float4" or "double precision" or "float8" => "Float",
            "boolean" or "bool" => "Boolean",
            "timestamp without time zone" or "timestamp" => "DateTime",
            "timestamp with time zone" or "timestamptz" => "DateTimeOffset",
            "json" or "jsonb" => "JSON",
            _ => "String",
        };
    }

    /// <inheritdoc />
    public string GetGraphQlTypeName(string dataType, bool isNullable = false)
        => $"{GetGraphQlType(dataType)}{(isNullable ? "" : "!")}";

    /// <inheritdoc />
    /// <remarks>
    /// Timestamp types are mapped to String for mutations to allow flexible date format input.
    /// </remarks>
    public string GetGraphQlInsertTypeName(string dataType, bool isNullable = false)
    {
        var normalized = dataType.ToLowerInvariant().Trim();
        if (normalized is "timestamp without time zone" or "timestamp" or
            "timestamp with time zone" or "timestamptz")
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
