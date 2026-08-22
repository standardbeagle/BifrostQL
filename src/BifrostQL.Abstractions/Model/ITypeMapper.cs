namespace BifrostQL.Core.Model;

/// <summary>Inclusive numeric bounds a column type can store, engine-decided.</summary>
public readonly record struct NumericValueRange(decimal Min, decimal Max);

/// <summary>Inclusive temporal bounds a column type can store, engine-decided.</summary>
public readonly record struct TemporalValueRange(DateTime Min, DateTime Max);

/// <summary>
/// The engine-invariant integer ranges shared by the <see cref="ITypeMapper"/>
/// default and dialect overrides that only extend it (a C# override cannot call
/// the interface default it replaces).
/// </summary>
public static class TypeMapperDefaults
{
    public static NumericValueRange? IntegerRange(string dataType) => dataType.Trim().ToLowerInvariant() switch
    {
        "int" or "integer" or "int4" => new NumericValueRange(int.MinValue, int.MaxValue),
        "smallint" or "int2" => new NumericValueRange(short.MinValue, short.MaxValue),
        "bigint" or "int8" => new NumericValueRange(long.MinValue, long.MaxValue),
        _ => null,
    };
}

/// <summary>
/// Maps database-specific data types to GraphQL type names.
/// Each database dialect provides its own implementation to handle
/// dialect-specific types (e.g., SQL Server's uniqueidentifier, PostgreSQL's jsonb).
/// </summary>
public interface ITypeMapper
{
    /// <summary>
    /// Maps a database data type string to its corresponding GraphQL type name.
    /// Returns the simple type name without nullable suffix (e.g., "Int", "String", "DateTime").
    /// </summary>
    /// <param name="dataType">The database data type (e.g., "int", "varchar", "jsonb").</param>
    /// <returns>The GraphQL type name, or "String" for unrecognized types.</returns>
    string GetGraphQlType(string dataType);

    /// <summary>
    /// Returns the GraphQL type name with nullable suffix applied.
    /// Non-nullable types get a "!" suffix.
    /// </summary>
    string GetGraphQlTypeName(string dataType, bool isNullable = false)
        => $"{GetGraphQlType(dataType)}{(isNullable ? "" : "!")}";

    /// <summary>
    /// Returns the GraphQL type name for insert/mutation inputs.
    /// Some types (e.g., datetime) may map differently in mutations than in queries.
    /// </summary>
    string GetGraphQlInsertTypeName(string dataType, bool isNullable = false)
        => $"{GetGraphQlType(dataType)}{(isNullable ? "" : "!")}";

    /// <summary>
    /// Returns the filter input type name for a given data type.
    /// </summary>
    string GetFilterInputTypeName(string dataType)
        => $"FilterType{GetGraphQlType(dataType)}Input";

    /// <summary>
    /// Returns true if the database type is recognized by this mapper.
    /// Unrecognized types fall back to String.
    /// </summary>
    bool IsSupported(string dataType);

    /// <summary>
    /// True when the type has large-object semantics IN THIS DIALECT: the value may be
    /// arbitrarily large, so grid/list clients should exclude it from bulk row selections
    /// and fetch it on demand. This is a per-dialect judgement, not a name lookup —
    /// PostgreSQL <c>text</c> and SQLite <c>TEXT</c> are those databases' ordinary string
    /// types and must return false, while SQL Server <c>text</c> and MySQL <c>text</c>
    /// are LOB types and must return true.
    /// </summary>
    bool IsLargeValue(string dataType) => false;

    /// <summary>
    /// The engine's storable range for an integer column type, or null when the
    /// engine imposes none this mapper can assert (unknown type, or an engine
    /// whose declared integer names don't bound storage, like SQLite). Drives
    /// server-side write validation so an out-of-range value is refused with a
    /// clear message instead of failing in the database. Defaults cover the
    /// engine-invariant names; dialects override for engine-specific ones
    /// (tinyint signedness, MySQL unsigned variants).
    /// </summary>
    NumericValueRange? GetIntegerRange(string dataType) => TypeMapperDefaults.IntegerRange(dataType);

    /// <summary>
    /// The engine's storable range for a date/time column type, or null when the
    /// engine accepts the full .NET range (or the type is unknown). Dialects
    /// override for narrow types (SQL Server datetime's 1753 floor, MySQL
    /// timestamp's 2038 ceiling) so a write outside them is refused server-side
    /// instead of failing in the database.
    /// </summary>
    TemporalValueRange? GetTemporalRange(string dataType) => null;
}
