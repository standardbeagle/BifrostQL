using BifrostQL.Core.QueryModel;

namespace BifrostQL.MySql;

/// <summary>
/// MySQL/MariaDB dialect implementation.
/// Uses backtick identifiers (`name`), LIMIT/OFFSET pagination,
/// CONCAT() for string concatenation, and LAST_INSERT_ID() for last inserted identity.
/// </summary>
public sealed class MySqlDialect : ISqlDialect
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly MySqlDialect Instance = new();

    /// <inheritdoc />
    public string EscapeIdentifier(string identifier) => $"`{identifier}`";

    /// <inheritdoc />
    public string TableReference(string? schema, string tableName) =>
        string.IsNullOrWhiteSpace(schema) ? $"`{tableName}`" : $"`{schema}`.`{tableName}`";

    /// <inheritdoc />
    /// <remarks>
    /// MySQL uses LIMIT/OFFSET syntax. Unlike SQL Server, ORDER BY is not required for pagination.
    /// MySQL uses CONCAT() for string concatenation instead of '+' or '||'.
    /// </remarks>
    public string Pagination(IEnumerable<string>? sortColumns, int? offset, int? limit)
    {
        var result = sortColumns?.Any() == true
            ? " ORDER BY " + string.Join(", ", sortColumns)
            : "";

        var actualLimit = limit switch { null => 100, -1 => null, _ => limit };
        if (actualLimit.HasValue)
            result += $" LIMIT {actualLimit}";

        var actualOffset = offset ?? 0;
        if (actualOffset > 0)
            result += $" OFFSET {actualOffset}";

        return result;
    }

    /// <inheritdoc />
    public string ParameterPrefix => "@";

    /// <inheritdoc />
    public string LastInsertedIdentity => "LAST_INSERT_ID()";

    /// <inheritdoc />
    public string LikePattern(string paramName, LikePatternType patternType) => patternType switch
    {
        LikePatternType.Contains => $"CONCAT('%', {paramName}, '%')",
        LikePatternType.StartsWith => $"CONCAT({paramName}, '%')",
        LikePatternType.EndsWith => $"CONCAT('%', {paramName})",
        _ => paramName
    };

    /// <inheritdoc />
    public string GetOperator(string op) => op switch
    {
        "_eq" => "=",
        "_neq" => "!=",
        "_lt" => "<",
        "_lte" => "<=",
        "_gt" => ">",
        "_gte" => ">=",
        "_contains" or "_starts_with" or "_ends_with" or "_like" => "LIKE",
        "_ncontains" or "_nstarts_with" or "_nends_with" or "_nlike" => "NOT LIKE",
        "_in" => "IN",
        "_nin" => "NOT IN",
        "_between" => "BETWEEN",
        "_nbetween" => "NOT BETWEEN",
        _ => "="
    };
}
