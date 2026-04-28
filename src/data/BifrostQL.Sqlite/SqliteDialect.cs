using BifrostQL.Core.QueryModel;

namespace BifrostQL.Sqlite;

/// <summary>
/// SQLite dialect implementation.
/// Uses double-quote identifiers ("name"), LIMIT/OFFSET pagination,
/// '||' for string concatenation, and last_insert_rowid() for last inserted identity.
/// </summary>
public sealed class SqliteDialect : StandardConcatDialectBase
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly SqliteDialect Instance = new();

    public SqliteDialect() : base('"', "last_insert_rowid()", " RETURNING rowid AS ID")
    {
    }

    /// <inheritdoc />
    public override string? UpsertSql(string tableRef, IReadOnlyList<string> keyColumns, IReadOnlyList<string> allColumns, IReadOnlyList<string> updateColumns)
    {
        if (keyColumns.Count == 0 || allColumns.Count == 0)
            return null;

        var columns = string.Join(",", allColumns.Select(EscapeIdentifier));
        var values = string.Join(",", allColumns.Select(c => $"@{c}"));
        var conflictKeys = string.Join(",", keyColumns.Select(EscapeIdentifier));

        if (updateColumns.Count == 0)
            return $"INSERT INTO {tableRef}({columns}) VALUES({values}) ON CONFLICT({conflictKeys}) DO NOTHING;";

        var setClause = string.Join(",", updateColumns.Select(c => $"{EscapeIdentifier(c)}=excluded.{EscapeIdentifier(c)}"));
        return $"INSERT INTO {tableRef}({columns}) VALUES({values}) ON CONFLICT({conflictKeys}) DO UPDATE SET {setClause};";
    }
}
