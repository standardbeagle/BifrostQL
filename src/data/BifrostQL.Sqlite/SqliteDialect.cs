using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

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
        // Parameter names must match what DbParameterBinder.AddParameters binds,
        // which sanitizes column names that are not valid parameter identifiers.
        var values = string.Join(",", allColumns.Select(c => $"@{SqlParameterNames.Sanitize(c)}"));
        var conflictKeys = string.Join(",", keyColumns.Select(EscapeIdentifier));

        if (updateColumns.Count == 0)
            return $"INSERT INTO {tableRef}({columns}) VALUES({values}) ON CONFLICT({conflictKeys}) DO NOTHING;";

        var setClause = string.Join(",", updateColumns.Select(c => $"{EscapeIdentifier(c)}=excluded.{EscapeIdentifier(c)}"));
        return $"INSERT INTO {tableRef}({columns}) VALUES({values}) ON CONFLICT({conflictKeys}) DO UPDATE SET {setClause};";
    }

    /// <inheritdoc />
    /// <remarks>
    /// SQLite full-text search uses an FTS5 external-content virtual table named
    /// <c>&lt;table&gt;_fts</c> that indexes the searchable columns and maps its rowid to
    /// the base table's integer primary key (the prerequisite the FTS guide documents). The
    /// predicate correlates by that key: <c>&lt;key&gt; IN (SELECT rowid FROM &lt;table&gt;_fts
    /// WHERE &lt;table&gt;_fts MATCH @term)</c>. Each term is bound as a double-quoted FTS5
    /// phrase (internal quotes doubled) so the injectable FTS5 MATCH grammar treats it as a
    /// literal phrase rather than operators; terms are ANDed at the SQL level to honor the
    /// pinned multi-term AND semantic. FTS5 matching is case-insensitive.
    ///
    /// FTS5 external content correlates on a single integer rowid, so a composite or absent
    /// primary key cannot be supported here — that fails closed with an actionable error
    /// rather than emitting a predicate that silently matches nothing.
    /// </remarks>
    public override ParameterizedSql SearchPredicate(FtsPredicateRequest request)
    {
        RequireSearchable(request);

        if (request.KeyColumnNames.Count != 1)
            throw new BifrostExecutionError(
                $"SQLite full-text search (_search) on table '{request.TableName}' requires a single-column " +
                "primary key: the FTS5 external-content index correlates rows by a single integer rowid, so a " +
                "composite or missing primary key is unsupported. Use a single INTEGER PRIMARY KEY, or remove " +
                "the 'search' metadata from this table.");

        var start = request.Parameters.Parameters.Count();
        var ftsTable = EscapeIdentifier($"{request.TableName}_fts");
        var rowId = EscapeIdentifier("rowid");
        var keyRef = request.TableAlias is null
            ? EscapeIdentifier(request.KeyColumnNames[0])
            : $"{EscapeIdentifier(request.TableAlias)}.{EscapeIdentifier(request.KeyColumnNames[0])}";

        var predicates = request.Terms.Select(term =>
        {
            var phrase = "\"" + term.Text.Replace("\"", "\"\"") + "\"";
            var p = request.Parameters.AddParameter(phrase);
            return $"{keyRef} IN (SELECT {rowId} FROM {ftsTable} WHERE {ftsTable} MATCH {p})";
        }).ToList();

        return new ParameterizedSql(
            string.Join(" AND ", predicates),
            request.Parameters.Parameters.Skip(start).ToList());
    }
}
