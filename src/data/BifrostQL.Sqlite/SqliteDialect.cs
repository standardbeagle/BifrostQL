using BifrostQL.Core.QueryModel;

namespace BifrostQL.Sqlite;

/// <summary>
/// SQLite dialect implementation.
/// Uses double-quote identifiers ("name"), LIMIT/OFFSET pagination,
/// '||' for string concatenation, and last_insert_rowid() for last inserted identity.
/// SQLite schemas are typically "main" and schema qualification is rarely needed.
/// </summary>
public sealed class SqliteDialect : ISqlDialect
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly SqliteDialect Instance = new();

    /// <inheritdoc />
    public string EscapeIdentifier(string identifier) => $"\"{identifier}\"";

    /// <inheritdoc />
    public string TableReference(string? schema, string tableName) =>
        string.IsNullOrWhiteSpace(schema) ? $"\"{tableName}\"" : $"\"{schema}\".\"{tableName}\"";

    /// <inheritdoc />
    /// <remarks>
    /// SQLite uses LIMIT/OFFSET syntax. ORDER BY is not required for pagination.
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
    public string LastInsertedIdentity => "last_insert_rowid()";

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
