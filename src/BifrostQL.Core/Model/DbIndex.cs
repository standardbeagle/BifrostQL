namespace BifrostQL.Core.Model;

/// <summary>
/// A database index on a table, as read from the catalog. Exposed through the
/// <c>_dbSchema</c> introspection surface so clients can make access-path-aware
/// decisions — above all choosing a default sort column that an index can serve.
/// On a large table, sorting by an unindexed column forces a full sort per page
/// (observed: 8.7s/page on 13M rows), while the leading column of any index
/// pages in milliseconds.
/// </summary>
public sealed record DbIndex
{
    /// <summary>Index name as it appears in the database catalog.</summary>
    public required string Name { get; init; }

    /// <summary>Schema of the table the index belongs to (db name).</summary>
    public required string TableSchema { get; init; }

    /// <summary>Table the index belongs to (db name).</summary>
    public required string TableName { get; init; }

    /// <summary>True when the index enforces uniqueness (includes PK indexes).</summary>
    public bool IsUnique { get; init; }

    /// <summary>
    /// True when the index defines the table's physical row order (SQL Server
    /// clustered index, MySQL/InnoDB primary key, Postgres after CLUSTER).
    /// The cheapest possible sort — prefer its leading column.
    /// </summary>
    public bool IsClustered { get; init; }

    /// <summary>True when the index backs the table's primary key constraint.</summary>
    public bool IsPrimaryKey { get; init; }

    /// <summary>
    /// Key columns in index key order (db names). Leading column first — the one
    /// an ORDER BY can use without a sort. Included (covering) columns are NOT
    /// listed; they carry no ordering.
    /// </summary>
    public required IReadOnlyList<string> ColumnNames { get; init; }
}
