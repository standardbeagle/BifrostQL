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
/// <param name="ForeignKeys">Foreign-key constraints discovered from the
/// database catalog. Required so the foreign-key relationship strategy can
/// build single-link/multi-link entries (including self-references); leaving
/// it empty falls back to name-based inference, which cannot detect self-FKs.</param>
public sealed record SchemaData(
    IDictionary<ColumnRef, List<ColumnConstraintDto>> ColumnConstraints,
    ColumnDto[] RawColumns,
    List<IDbTable> Tables,
    IReadOnlyList<DbForeignKey> ForeignKeys
)
{
    public SchemaData(
        IDictionary<ColumnRef, List<ColumnConstraintDto>> columnConstraints,
        ColumnDto[] rawColumns,
        List<IDbTable> tables)
        : this(columnConstraints, rawColumns, tables, Array.Empty<DbForeignKey>())
    {
    }
}
