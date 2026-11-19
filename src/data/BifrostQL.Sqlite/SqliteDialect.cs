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
    private static readonly RawSqlValidator RawSqlValidator = new(RawSqlLexicon.Sqlite);

    /// <inheritdoc />
    public override RawSqlValidationResult ValidateReadOnlySql(string sql) => RawSqlValidator.Validate(sql);

    /// <inheritdoc />
    /// <remarks>
    /// <c>PRAGMA query_only</c> makes SQLite refuse every change to the database file.
    /// It is connection state, and pooled connections keep it, so the scope turns it
    /// back off after the rollback. The transaction is deferred: <c>BEGIN IMMEDIATE</c>
    /// takes a write lock a read-only scope has no use for.
    /// </remarks>
    public override async Task<ReadOnlySqlScope> BeginReadOnlyScopeAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        var sqliteConnection = connection as Microsoft.Data.Sqlite.SqliteConnection
            ?? throw new ArgumentException($"SqliteDialect needs a SqliteConnection, got {connection.GetType().Name}.", nameof(connection));
        await SetQueryOnlyAsync(sqliteConnection, true, cancellationToken);
        try
        {
            // No async overload takes `deferred`; a deferred BEGIN takes no lock and does
            // no file I/O (the first read does), so this sync call never blocks.
            var transaction = sqliteConnection.BeginTransaction(deferred: true);
            return new ReadOnlySqlScope(transaction, c => SetQueryOnlyAsync(c, false, CancellationToken.None));
        }
        catch
        {
            await SetQueryOnlyAsync(sqliteConnection, false, CancellationToken.None);
            throw;
        }
    }

    private static async Task SetQueryOnlyAsync(System.Data.Common.DbConnection connection, bool on, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = on ? "PRAGMA query_only = ON" : "PRAGMA query_only = OFF";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

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
    /// SQLite types values, not columns, and the reader returns each value by its storage
    /// class, so the rendering is chosen per row on <c>typeof</c>: an integer as its
    /// digits, a real as .NET's shortest round-trip text (the first of 15/16/17
    /// significant digits that reads back equal, re-laid out in .NET notation; in 600
    /// seeded doubles every mismatch was a 17-digit value whose last digit SQLite's printf
    /// rounds wrongly, which is why a REAL-affinity key is refused and only a real stored in
    /// a column of another affinity takes this path), a blob as
    /// base64 (what the reader turns a <c>byte[]</c> into), and text as stored.
    /// </remarks>
    protected override KeySegmentText RenderKeySegment(ObjectKeySegmentColumn segment)
    {
        // A REAL-affinity column holds floats by design, and SQLite's printf and text-to-real
        // are not correctly rounded at 17 digits: about one random double in seventy renders
        // one digit off .NET's shortest text. Refused rather than listed out of order.
        var type = KeyTypeName(segment);
        if (type.Contains("real", StringComparison.Ordinal) || type.Contains("floa", StringComparison.Ordinal)
            || type.Contains("doub", StringComparison.Ordinal))
            throw UnsupportedKeyType(segment, "SQLite has no correctly rounded shortest float text");

        var x = segment.ColumnExpression;
        var real = DotNetFloatText(
            ShortestFloatCascade(x, new[] { 14, 15, 16 },
                (value, digits) => $"printf('%!.{digits}e', {value})",
                (candidate, value) => $"CAST({candidate} AS REAL) = {value}"),
            single: false);
        return KeySegmentText.Escaped(
            $"CASE typeof({x}) WHEN 'integer' THEN CAST({x} AS TEXT)"
            + $" WHEN 'real' THEN {ReplaceReservedChars(real, ".+")}"
            + $" WHEN 'blob' THEN {ReplaceReservedChars(Base64(x), "+/=")}"
            + $" ELSE {PercentEncodeUtf8(x)} END");
    }

    private const string HexDigits = "'0123456789ABCDEF'";
    private const string Base64Alphabet = "'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'";

    /// <summary>
    /// Base64 of a blob, as <see cref="Convert.ToBase64String(byte[])"/> writes it: a
    /// recursive CTE over <c>hex()</c> takes three bytes (six hex digits) per step and
    /// emits four alphabet characters, padding a short last group with <c>=</c>.
    /// </summary>
    private static string Base64(string blobExpression)
    {
        const string group = "substr(okb.h, i, 6)";
        var padded = $"substr({group} || '00000', 1, 6)";
        var value = string.Join(" + ", Enumerable.Range(0, 6).Select(k =>
            $"(instr({HexDigits}, substr({padded}, {k + 1}, 1)) - 1) * {1 << (4 * (5 - k))}"));
        string Char(string sextet) => $"substr({Base64Alphabet}, (({sextet}) & 63) + 1, 1)";
        var v = $"({value})";
        return "(WITH RECURSIVE okb64(i, o) AS (SELECT 1, '' UNION ALL SELECT i + 6, o || "
            + Char($"{v} >> 18") + " || " + Char($"{v} >> 12")
            + $" || CASE WHEN length({group}) >= 4 THEN {Char($"{v} >> 6")} ELSE '=' END"
            + $" || CASE WHEN length({group}) = 6 THEN {Char(v)} ELSE '=' END"
            + $" FROM okb64, (SELECT hex({blobExpression}) AS h) AS okb WHERE i <= length(okb.h))"
            + " SELECT o FROM okb64 ORDER BY i DESC LIMIT 1)";
    }

    /// <inheritdoc />
    /// <remarks>
    /// SQLite has neither a byte loop nor a regex, so a recursive CTE walks <c>hex()</c> of
    /// the UTF-8 text two digits at a time, appending each byte as itself or as
    /// <c>%XX</c>, and keeps the last (complete) row.
    /// </remarks>
    protected override string PercentEncodeUtf8(string textExpression) =>
        "(WITH RECURSIVE okw(i, o) AS (SELECT 1, '' UNION ALL SELECT i + 2, o || CASE WHEN "
        + "substr(okh.h, i, 2) IN ('2D', '5F') OR substr(okh.h, i, 2) BETWEEN '30' AND '39' "
        + "OR substr(okh.h, i, 2) BETWEEN '41' AND '5A' OR substr(okh.h, i, 2) BETWEEN '61' AND '7A' "
        + $"THEN char((instr({HexDigits}, substr(okh.h, i, 1)) - 1) * 16 "
        + $"+ instr({HexDigits}, substr(okh.h, i + 1, 1)) - 1) "
        + "ELSE '%' || substr(okh.h, i, 2) END "
        + $"FROM okw, (SELECT hex({textExpression}) AS h) AS okh WHERE i <= length(okh.h)) "
        + "SELECT o FROM okw ORDER BY i DESC LIMIT 1)";

    /// <inheritdoc />
    protected override string KeyTextLength(string text) => $"length({text})";

    /// <inheritdoc />
    protected override string KeyTextPosition(char needle, string text) => $"instr({text}, '{needle}')";

    /// <inheritdoc />
    protected override string KeyTextSubstring(string text, string start, string length) => $"substr({text}, {start}, {length})";

    /// <inheritdoc />
    protected override string KeyIntText(string integer) => $"CAST({integer} AS TEXT)";

    /// <inheritdoc />
    protected override string KeyTextInt(string text) => $"CAST({text} AS INTEGER)";

    /// <inheritdoc />
    protected override string KeyTrimLeadingZeros(string text) => $"ltrim({text}, '0')";

    /// <inheritdoc />
    protected override string KeyTrimTrailingZeros(string text) => $"rtrim({text}, '0')";

    /// <inheritdoc />
    /// <remarks>SQLite's <c>BINARY</c> collation is memcmp over the UTF-8 text.</remarks>
    protected override string BinaryOrder(string textExpression) => $"({textExpression}) COLLATE BINARY";

    /// <inheritdoc />
    /// <remarks>
    /// <c>datetime(&lt;source&gt;, &lt;amount&gt; || ' days')</c>. SQLite's <c>datetime()</c> takes a
    /// text modifier like <c>'3 days'</c>; concatenating the bound amount with the fixed unit word
    /// builds that modifier while keeping the amount a parameter (never interpolated).
    /// </remarks>
    protected override string LowerDateAdd(SqlExpr.DateAdd node, Func<SqlExpr, string> lowerOperand)
    {
        var amount = lowerOperand(node.Amount);
        var source = lowerOperand(node.Source);
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
    protected override string LowerDateDiff(SqlExpr.DateDiff node, Func<SqlExpr, string> lowerOperand)
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

        var start = lowerOperand(node.Start);
        var end = lowerOperand(node.End);
        return $"CAST((julianday({end}) - julianday({start})) * {unitsPerDay} AS INTEGER)";
    }

    /// <inheritdoc />
    /// <remarks><c>CAST(strftime('%Y', &lt;source&gt;) AS INTEGER)</c> — strftime yields a
    /// zero-padded string, cast to an integer for numeric parity with the other dialects.</remarks>
    protected override string LowerDatePart(SqlExpr.DatePart node, Func<SqlExpr, string> lowerOperand)
    {
        var source = lowerOperand(node.Source);
        return $"CAST(strftime('{StrftimeField(node.Unit)}', {source}) AS INTEGER)";
    }

    /// <inheritdoc />
    /// <remarks><c>json_extract(&lt;source&gt;, '$.a.b')</c>. The path is spliced from
    /// <see cref="JsonPath"/>'s validated segments.</remarks>
    protected override string LowerJsonGet(SqlExpr.JsonGet node, Func<SqlExpr, string> lowerOperand)
    {
        var source = lowerOperand(node.Source);
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
            var p = request.Parameters.AddParameter(phrase).Name;
            return $"{keyRef} IN (SELECT {rowId} FROM {ftsTable} WHERE {ftsTable} MATCH {p})";
        }).ToList();

        return new ParameterizedSql(
            string.Join(" AND ", predicates),
            request.Parameters.Parameters.Skip(start).ToList());
    }
}
