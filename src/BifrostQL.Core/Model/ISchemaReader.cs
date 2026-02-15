using System.Data.Common;

namespace BifrostQL.Core.Model;

/// <summary>
/// Database-agnostic interface for reading schema metadata.
/// Implementations provide database-specific queries for constraints, columns, and tables.
/// </summary>
public interface ISchemaReader
{
    /// <summary>
    /// Reads schema metadata from the database and returns a dictionary structure
    /// containing constraints, columns, and tables.
    /// </summary>
    /// <param name="connection">Open database connection</param>
    /// <returns>Schema data including column constraints, columns, and tables</returns>
    Task<SchemaData> ReadSchemaAsync(DbConnection connection);
}

/// <summary>
/// Container for database schema metadata read from the database.
/// </summary>
public sealed record SchemaData(
    IDictionary<ColumnRef, List<ColumnConstraintDto>> ColumnConstraints,
    ColumnDto[] RawColumns,
    List<IDbTable> Tables
);
