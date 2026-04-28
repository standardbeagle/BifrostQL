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
    /// MySQL uses CONCAT() function instead of || operator.
    /// </remarks>
    public override string LikePattern(string paramName, LikePatternType patternType) => patternType switch
    {
        LikePatternType.Contains => $"CONCAT('%', {paramName}, '%')",
        LikePatternType.StartsWith => $"CONCAT({paramName}, '%')",
        LikePatternType.EndsWith => $"CONCAT('%', {paramName})",
        _ => paramName
    };
}
