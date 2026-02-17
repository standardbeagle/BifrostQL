using BifrostQL.Core.QueryModel;

namespace BifrostQL.Ngsql;

/// <summary>
/// PostgreSQL dialect implementation.
/// Uses double-quote identifiers ("name"), LIMIT/OFFSET pagination,
/// '||' for string concatenation, and lastval() for last inserted identity.
/// </summary>
public sealed class PostgresDialect : ISqlDialect
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly PostgresDialect Instance = new();

    /// <inheritdoc />
    public string EscapeIdentifier(string identifier) => $"\"{identifier}\"";

    /// <inheritdoc />
    public string TableReference(string? schema, string tableName) =>
        string.IsNullOrWhiteSpace(schema) ? $"\"{tableName}\"" : $"\"{schema}\".\"{tableName}\"";

    /// <inheritdoc />
    /// <remarks>
    /// PostgreSQL supports LIMIT/OFFSET without requiring ORDER BY.
    /// When no sort columns are provided, the ORDER BY clause is omitted entirely.
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
    public string LastInsertedIdentity => "lastval()";

    /// <inheritdoc />
    public string LikePattern(string paramName, LikePatternType patternType) => patternType switch
    {
        LikePatternType.Contains => $"'%' || {paramName} || '%'",
        LikePatternType.StartsWith => $"{paramName} || '%'",
        LikePatternType.EndsWith => $"'%' || {paramName}",
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
