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
        var columnList = string.Join(", ", request.ColumnNames.Select(EscapeIdentifier));

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
