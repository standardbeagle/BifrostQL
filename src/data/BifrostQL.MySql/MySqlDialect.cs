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
    protected override string LowerDateAdd(SqlExpr.DateAdd node, IDbTable table, SqlParameterCollection parameters)
    {
        var amount = LowerExpression(node.Amount, table, parameters);
        var source = LowerExpression(node.Source, table, parameters);
        return $"DATE_ADD({source}, INTERVAL {amount} {UnitKeyword(node.Unit)})";
    }

    /// <inheritdoc />
    /// <remarks><c>TIMESTAMPDIFF(DAY, &lt;start&gt;, &lt;end&gt;)</c> — native for every unit,
    /// including whole months/years.</remarks>
    protected override string LowerDateDiff(SqlExpr.DateDiff node, IDbTable table, SqlParameterCollection parameters)
    {
        var start = LowerExpression(node.Start, table, parameters);
        var end = LowerExpression(node.End, table, parameters);
        return $"TIMESTAMPDIFF({UnitKeyword(node.Unit)}, {start}, {end})";
    }

    /// <inheritdoc />
    /// <remarks><c>EXTRACT(YEAR FROM &lt;source&gt;)</c>.</remarks>
    protected override string LowerDatePart(SqlExpr.DatePart node, IDbTable table, SqlParameterCollection parameters)
    {
        var source = LowerExpression(node.Source, table, parameters);
        return $"EXTRACT({UnitKeyword(node.Unit)} FROM {source})";
    }

    /// <inheritdoc />
    /// <remarks><c>JSON_UNQUOTE(JSON_EXTRACT(&lt;source&gt;, '$.a.b'))</c> — JSON_EXTRACT returns a
    /// quoted JSON scalar, JSON_UNQUOTE strips the quotes to the raw text. The path is spliced from
    /// <see cref="JsonPath"/>'s validated segments.</remarks>
    protected override string LowerJsonGet(SqlExpr.JsonGet node, IDbTable table, SqlParameterCollection parameters)
    {
        var source = LowerExpression(node.Source, table, parameters);
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
            var p = request.Parameters.AddParameter(phrase);
            return $"MATCH({columnList}) AGAINST({p} IN BOOLEAN MODE)";
        }).ToList();

        return new ParameterizedSql(
            string.Join(" AND ", predicates),
            request.Parameters.Parameters.Skip(start).ToList());
    }
}
