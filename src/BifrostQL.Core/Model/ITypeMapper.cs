namespace BifrostQL.Core.Model;

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
}
