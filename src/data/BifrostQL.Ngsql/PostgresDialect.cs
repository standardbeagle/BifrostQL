using BifrostQL.Core.QueryModel;

namespace BifrostQL.Ngsql;

public sealed class PostgresDialect : ISqlDialect
{
    public static readonly PostgresDialect Instance = new();

    public string EscapeIdentifier(string identifier) => $"\"{identifier}\"";

    public string TableReference(string? schema, string tableName) =>
        string.IsNullOrWhiteSpace(schema) ? $"\"{tableName}\"" : $"\"{schema}\".\"{tableName}\"";

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

    public string ParameterPrefix => "@";
    public string LastInsertedIdentity => "lastval()";

    public string LikePattern(string paramName, LikePatternType patternType) => patternType switch
    {
        LikePatternType.Contains => $"'%' || {paramName} || '%'",
        LikePatternType.StartsWith => $"{paramName} || '%'",
        LikePatternType.EndsWith => $"'%' || {paramName}",
        _ => paramName
    };

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
