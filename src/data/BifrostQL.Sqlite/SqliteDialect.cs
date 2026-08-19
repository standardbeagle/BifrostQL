using BifrostQL.Core.Model;
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

    /// <summary>SQLite <c>datetime()</c> modifier plural (e.g. <c>'3 days'</c>).</summary>
    private static string DateTimeModifierUnit(DateUnit unit) => unit switch
    {
        DateUnit.Year => "years",
        DateUnit.Month => "months",
        DateUnit.Day => "days",
        DateUnit.Hour => "hours",
        DateUnit.Minute => "minutes",
        DateUnit.Second => "seconds",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null)
    };

    /// <summary><c>strftime</c> format code for a single field.</summary>
    private static string StrftimeField(DateUnit unit) => unit switch
    {
        DateUnit.Year => "%Y",
        DateUnit.Month => "%m",
        DateUnit.Day => "%d",
        DateUnit.Hour => "%H",
        DateUnit.Minute => "%M",
        DateUnit.Second => "%S",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null)
    };

    /// <inheritdoc />
    /// <remarks>
    /// <c>datetime(&lt;source&gt;, &lt;amount&gt; || ' days')</c>. SQLite's <c>datetime()</c> takes a
    /// text modifier like <c>'3 days'</c>; concatenating the bound amount with the fixed unit word
    /// builds that modifier while keeping the amount a parameter (never interpolated).
    /// </remarks>
    protected override string LowerDateAdd(SqlExpr.DateAdd node, IDbTable table, SqlParameterCollection parameters)
    {
        var amount = LowerExpression(node.Amount, table, parameters);
        var source = LowerExpression(node.Source, table, parameters);
        return $"datetime({source}, {amount} || ' {DateTimeModifierUnit(node.Unit)}')";
    }

    /// <inheritdoc />
    /// <remarks>
    /// SQLite has no <c>DATEDIFF</c>. For the Julian-day-computable units (day/hour/minute/second)
    /// the difference is <c>CAST((julianday(end) - julianday(start)) * &lt;unitsPerDay&gt; AS INTEGER)</c>.
    /// Whole months/years cannot be counted from a Julian-day delta (calendar boundaries vary), so
    /// they fail fast with <see cref="SqlExprLoweringNotSupportedException"/> rather than emit a
    /// silently-wrong approximation.
    /// </remarks>
    protected override string LowerDateDiff(SqlExpr.DateDiff node, IDbTable table, SqlParameterCollection parameters)
    {
        var unitsPerDay = node.Unit switch
        {
            DateUnit.Day => 1,
            DateUnit.Hour => 24,
            DateUnit.Minute => 1440,
            DateUnit.Second => 86400,
            _ => throw new SqlExprLoweringNotSupportedException(
                nameof(SqlExpr.DateDiff), "SQLite",
                $"whole-{node.Unit.ToString().ToLowerInvariant()} difference cannot be computed exactly " +
                "from a Julian-day delta (calendar boundaries vary); use DatePart-based arithmetic instead.")
        };

        var start = LowerExpression(node.Start, table, parameters);
        var end = LowerExpression(node.End, table, parameters);
        return $"CAST((julianday({end}) - julianday({start})) * {unitsPerDay} AS INTEGER)";
    }

    /// <inheritdoc />
    /// <remarks><c>CAST(strftime('%Y', &lt;source&gt;) AS INTEGER)</c> — strftime yields a
    /// zero-padded string, cast to an integer for numeric parity with the other dialects.</remarks>
    protected override string LowerDatePart(SqlExpr.DatePart node, IDbTable table, SqlParameterCollection parameters)
    {
        var source = LowerExpression(node.Source, table, parameters);
        return $"CAST(strftime('{StrftimeField(node.Unit)}', {source}) AS INTEGER)";
    }

    /// <inheritdoc />
    /// <remarks><c>json_extract(&lt;source&gt;, '$.a.b')</c>. The path is spliced from
    /// <see cref="JsonPath"/>'s validated segments.</remarks>
    protected override string LowerJsonGet(SqlExpr.JsonGet node, IDbTable table, SqlParameterCollection parameters)
    {
        var source = LowerExpression(node.Source, table, parameters);
        return $"json_extract({source}, '{node.Path.ToDollarPath()}')";
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
        var keyRef = SearchColumnRef(request, request.KeyColumnNames[0]);

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
