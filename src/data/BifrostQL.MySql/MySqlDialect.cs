using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.MySql;

/// <summary>
/// MySQL/MariaDB dialect implementation.
/// Uses backtick identifiers (`name`), LIMIT/OFFSET pagination,
/// CONCAT() for string concatenation, and LAST_INSERT_ID() for last inserted identity.
/// </summary>
public sealed class MySqlDialect : LimitOffsetDialectBase
{
    private static readonly RawSqlValidator RawSqlValidator = new(RawSqlLexicon.MySql);

    /// <inheritdoc />
    public override RawSqlValidationResult ValidateReadOnlySql(string sql) => RawSqlValidator.Validate(sql);

    /// <inheritdoc />
    /// <remarks><c>START TRANSACTION READ ONLY</c>: MySQL refuses writes to every
    /// non-temporary table, including writes inside a called stored function.</remarks>
    public override async Task<ReadOnlySqlScope> BeginReadOnlyScopeAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        var mySqlConnection = connection as MySqlConnector.MySqlConnection
            ?? throw new ArgumentException($"MySqlDialect needs a MySqlConnection, got {connection.GetType().Name}.", nameof(connection));
        var transaction = await mySqlConnection.BeginTransactionAsync(System.Data.IsolationLevel.Unspecified, isReadOnly: true, cancellationToken);
        return new ReadOnlySqlScope(transaction);
    }

    /// <summary>Shared singleton instance.</summary>
    public static readonly MySqlDialect Instance = new();

    public MySqlDialect() : base('`', "CONCAT", "LAST_INSERT_ID()")
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// MySQL/InnoDB locks a selected row with the standard trailing <c>FOR UPDATE</c>
    /// clause, held until the transaction ends. The change-history before-image read uses
    /// it so a concurrent writer blocks instead of committing between the pre-image read
    /// and the UPDATE it precedes.
    /// </remarks>
    public override string UpdateLockClause => " FOR UPDATE";

    /// <inheritdoc />
    /// <remarks>MySQL has no <c>DEFAULT VALUES</c>; an empty column list is its spelling.</remarks>
    public override string EmptyInsertValues => "() VALUES()";

    /// <inheritdoc />
    /// <remarks>
    /// MySQL uses CONCAT() function instead of || operator.
    /// </remarks>
    public override string LikePattern(string paramName, LikePatternType patternType) => patternType switch
    {
        LikePatternType.Contains => $"CONCAT('%', {paramName}, '%')",
        LikePatternType.StartsWith => $"CONCAT({paramName}, '%')",
        LikePatternType.EndsWith => $"CONCAT('%', {paramName})",
        _ => paramName
    };

    /// <inheritdoc />
    /// <remarks>
    /// MySQL string literals treat backslash as an escape character, so naming
    /// backslash as the LIKE escape requires doubling it in the SQL text:
    /// <c>ESCAPE '\\'</c>.
    /// </remarks>
    public override string LikeEscapeClause => " ESCAPE '\\\\'";

    /// <inheritdoc />
    /// <remarks>
    /// MySQL has no infix string-concatenation operator (<c>||</c> is logical-OR there), so a
    /// <see cref="SqlExpr.Concat"/> must render as the <c>CONCAT(...)</c> function — the same
    /// per-dialect concat distinction <see cref="LikePattern"/> already makes.
    /// </remarks>
    protected override string LowerConcat(IReadOnlyList<string> loweredParts)
        => $"CONCAT({string.Join(", ", loweredParts)})";

    /// <inheritdoc />
    /// <remarks>
    /// The observed read type wins where MySqlConnector decides it from connection options
    /// (<c>TINYINT(1)</c> read as <see cref="bool"/>, <c>CHAR(36)</c> as <see cref="Guid"/>).
    /// Otherwise by declared type: integers and decimals by their text, dates as
    /// <c>DateTime</c>'s round-trip form (seven fraction digits), <c>TIME</c> as
    /// <c>TimeSpan</c>'s constant form, binary as base64, <c>DOUBLE</c> from MySQL's
    /// shortest round-trip text (8.0+) re-laid out in .NET notation. <c>FLOAT</c> is
    /// refused: MySQL prints it with six significant digits, not the shortest text that
    /// reads back as the same single, and has no formatter that does.
    /// </remarks>
    protected override KeySegmentText RenderKeySegment(ObjectKeySegmentColumn segment)
    {
        var x = segment.ColumnExpression;
        if (segment.ReadClrType == typeof(bool))
            return KeySegmentText.Ascii($"CASE WHEN {x} <> 0 THEN 'True' ELSE 'False' END");
        if (segment.ReadClrType == typeof(Guid))
            return KeySegmentText.Ascii($"LOWER(CAST({x} AS CHAR))");

        switch (KeyTypeName(segment))
        {
            case "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" or "year":
                return KeySegmentText.Ascii($"CAST({x} AS CHAR)");
            case "bit":
                return KeySegmentText.Ascii($"CAST({x} + 0 AS CHAR)");
            case "decimal" or "numeric" or "fixed" or "dec":
                RequireClrDecimalScale(segment);
                return KeySegmentText.Ascii($"CAST({x} AS CHAR)", ".");
            case "double" or "double precision" or "real":
                return KeySegmentText.Ascii(DotNetFloatText($"CAST({x} AS CHAR(64))", single: false), ".+");
            case "float":
                throw UnsupportedKeyType(segment, "MySQL has no shortest round-trip text for a single-precision value");
            case "date" or "datetime" or "timestamp":
                return KeySegmentText.Ascii($"CONCAT(DATE_FORMAT({x}, '%Y-%m-%dT%H:%i:%s.%f'), '0')", ":.");
            case "time":
                return KeySegmentText.Ascii(TimeSpanText(x), ":.");
            case "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext" or "enum" or "set":
                return KeySegmentText.AnyText(x);
            case "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob":
                return KeySegmentText.Ascii($"REPLACE(TO_BASE64({x}), CHAR(10), '')", "+/=");
            default:
                throw UnsupportedKeyType(segment);
        }
    }

    /// <summary>
    /// A <c>TIME</c> as <c>TimeSpan.ToString("c")</c>: <c>[-][d.]hh:mm:ss[.fffffff]</c>.
    /// <c>HOUR</c>/<c>MINUTE</c>/<c>SECOND</c>/<c>MICROSECOND</c> are magnitudes, so the sign
    /// comes from the value's own text (<c>TIME_TO_SEC</c> loses it below one second).
    /// </summary>
    private static string TimeSpanText(string x) =>
        $"CONCAT(CASE WHEN CAST({x} AS CHAR) LIKE '-%' AND (TIME_TO_SEC({x}) <> 0 OR MICROSECOND({x}) <> 0) THEN '-' ELSE '' END, "
        + $"CASE WHEN HOUR({x}) >= 24 THEN CONCAT(HOUR({x}) DIV 24, '.') ELSE '' END, "
        + $"LPAD(HOUR({x}) MOD 24, 2, '0'), ':', LPAD(MINUTE({x}), 2, '0'), ':', LPAD(SECOND({x}), 2, '0'), "
        + $"CASE WHEN MICROSECOND({x}) > 0 THEN CONCAT('.', LPAD(MICROSECOND({x}), 6, '0'), '0') ELSE '' END)";

    /// <inheritdoc />
    /// <remarks>
    /// Byte-wise <c>REPLACE</c> over the utf8mb4 bytes as a binary string: <c>%</c> first,
    /// then every other byte outside <c>[A-Za-z0-9_-]</c> with its <c>%XX</c> token. No
    /// replacement output holds a byte a later <c>REPLACE</c> matches. Plain string
    /// functions only, so it runs on MySQL 5.7 and MariaDB alike (a regex backreference
    /// is spelled differently on each).
    /// </remarks>
    protected override string PercentEncodeUtf8(string textExpression)
    {
        var sql = $"REPLACE(CAST(CONVERT({textExpression} USING utf8mb4) AS BINARY), '%', '%25')";
        for (var b = 0; b < 256; b++)
        {
            if (b == '%' || Array.IndexOf(UnreservedKeyBytes, (byte)b) >= 0)
                continue;
            sql = $"REPLACE({sql}, UNHEX('{b:X2}'), '%{b:X2}')";
        }
        return sql;
    }

    /// <inheritdoc />
    protected override string KeyTextLength(string text) => $"CHAR_LENGTH({text})";

    /// <inheritdoc />
    protected override string KeyTextPosition(char needle, string text) => $"LOCATE('{needle}', {text})";

    /// <inheritdoc />
    protected override string KeyTextSubstring(string text, string start, string length) => $"SUBSTRING({text}, {start}, {length})";

    /// <inheritdoc />
    protected override string KeyIntText(string integer) => $"CAST({integer} AS CHAR)";

    /// <inheritdoc />
    protected override string KeyTextInt(string text) => $"CAST({text} AS SIGNED)";

    /// <inheritdoc />
    protected override string KeyTrimLeadingZeros(string text) => $"TRIM(LEADING '0' FROM {text})";

    /// <inheritdoc />
    protected override string KeyTrimTrailingZeros(string text) => $"TRIM(TRAILING '0' FROM {text})";

    /// <inheritdoc />
    /// <remarks>A binary string compares bytes, independent of the connection's collation.</remarks>
    protected override string BinaryOrder(string textExpression) => $"CAST({textExpression} AS BINARY)";

    /// <inheritdoc />
    /// <remarks>
    /// MySQL's CAST accepts only its own target types: <c>CHAR</c> for text and <c>SIGNED</c>
    /// for integers (the ANSI <c>TEXT</c>/<c>INTEGER</c> the base emits are rejected by MySQL's
    /// CAST grammar).
    /// </remarks>
    protected override string RenderCastType(SqlExprType type) => type switch
    {
        SqlExprType.Text => "CHAR",
        SqlExprType.Int => "SIGNED",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    /// <summary>MySQL keyword used for <c>INTERVAL</c>, <c>TIMESTAMPDIFF</c>, and <c>EXTRACT</c>.</summary>
    private static string UnitKeyword(DateUnit unit) => unit switch
    {
        DateUnit.Year => "YEAR",
        DateUnit.Month => "MONTH",
        DateUnit.Day => "DAY",
        DateUnit.Hour => "HOUR",
        DateUnit.Minute => "MINUTE",
        DateUnit.Second => "SECOND",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null)
    };

    /// <inheritdoc />
    /// <remarks><c>DATE_ADD(&lt;source&gt;, INTERVAL &lt;amount&gt; DAY)</c>. The amount lowers to a
    /// bound parameter; the unit is a fixed keyword.</remarks>
    protected override string LowerDateAdd(SqlExpr.DateAdd node, Func<SqlExpr, string> lowerOperand)
    {
        var amount = lowerOperand(node.Amount);
        var source = lowerOperand(node.Source);
        return $"DATE_ADD({source}, INTERVAL {amount} {UnitKeyword(node.Unit)})";
    }

    /// <inheritdoc />
    /// <remarks><c>TIMESTAMPDIFF(DAY, &lt;start&gt;, &lt;end&gt;)</c> — native for every unit,
    /// including whole months/years.</remarks>
    protected override string LowerDateDiff(SqlExpr.DateDiff node, Func<SqlExpr, string> lowerOperand)
    {
        var start = lowerOperand(node.Start);
        var end = lowerOperand(node.End);
        return $"TIMESTAMPDIFF({UnitKeyword(node.Unit)}, {start}, {end})";
    }

    /// <inheritdoc />
    /// <remarks><c>EXTRACT(YEAR FROM &lt;source&gt;)</c>.</remarks>
    protected override string LowerDatePart(SqlExpr.DatePart node, Func<SqlExpr, string> lowerOperand)
    {
        var source = lowerOperand(node.Source);
        return $"EXTRACT({UnitKeyword(node.Unit)} FROM {source})";
    }

    /// <inheritdoc />
    /// <remarks><c>JSON_UNQUOTE(JSON_EXTRACT(&lt;source&gt;, '$.a.b'))</c> — JSON_EXTRACT returns a
    /// quoted JSON scalar, JSON_UNQUOTE strips the quotes to the raw text. The path is spliced from
    /// <see cref="JsonPath"/>'s validated segments.</remarks>
    protected override string LowerJsonGet(SqlExpr.JsonGet node, Func<SqlExpr, string> lowerOperand)
    {
        var source = lowerOperand(node.Source);
        return $"JSON_UNQUOTE(JSON_EXTRACT({source}, '{node.Path.ToDollarPath()}'))";
    }

    /// <inheritdoc />
    /// <remarks>
    /// MySQL full-text search uses <c>MATCH(col1, col2) AGAINST(… IN BOOLEAN MODE)</c>
    /// against a FULLTEXT index on the searchable columns (the prerequisite the FTS guide
    /// documents). Boolean mode is chosen so the pinned AND semantic can be honored: each
    /// term is bound as a quoted phrase (internal quotes doubled) which neutralizes boolean
    /// operators (<c>+ - * " ( )</c>) and matches the word/words literally, and the terms
    /// are ANDed at the SQL level rather than relying on natural-language mode's OR-ish
    /// scoring. MySQL full-text matching is case-insensitive by the column collation.
    /// </remarks>
    public override ParameterizedSql SearchPredicate(FtsPredicateRequest request)
    {
        RequireSearchable(request);
        var start = request.Parameters.Parameters.Count();
        // Alias-qualified: MATCH's columns must all resolve to the one indexed table,
        // which a bare name does not guarantee once another table is in scope.
        var columnList = string.Join(", ", request.ColumnNames.Select(c => SearchColumnRef(request, c)));

        var predicates = request.Terms.Select(term =>
        {
            var phrase = "\"" + term.Text.Replace("\"", "\"\"") + "\"";
            var p = request.Parameters.AddParameter(phrase).Name;
            return $"MATCH({columnList}) AGAINST({p} IN BOOLEAN MODE)";
        }).ToList();

        return new ParameterizedSql(
            string.Join(" AND ", predicates),
            request.Parameters.Parameters.Skip(start).ToList());
    }
}
