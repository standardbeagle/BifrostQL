namespace BifrostQL.Core.QueryModel;

/// <summary>
/// SQL Server dialect implementation.
/// Uses bracket identifiers ([name]), OFFSET/FETCH NEXT pagination (requires ORDER BY),
/// '+' for string concatenation, and SCOPE_IDENTITY() for last inserted identity.
/// </summary>
public sealed class SqlServerDialect : ISqlDialect
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly SqlServerDialect Instance = new();

    /// <inheritdoc />
    public string EscapeIdentifier(string identifier) => $"[{identifier}]";

    /// <inheritdoc />
    public string TableReference(string? schema, string tableName) =>
        string.IsNullOrWhiteSpace(schema) ? $"[{tableName}]" : $"[{schema}].[{tableName}]";

    /// <inheritdoc />
    /// <remarks>
    /// SQL Server requires ORDER BY for OFFSET/FETCH. When no sort columns are provided,
    /// ORDER BY (SELECT NULL) is used as a no-op ordering to satisfy the syntax requirement.
    /// </remarks>
    public string Pagination(IEnumerable<string>? sortColumns, int? offset, int? limit)
    {
        var orderBy = sortColumns?.Any() == true
            ? " ORDER BY " + string.Join(", ", sortColumns)
            : " ORDER BY (SELECT NULL)";

        orderBy += $" OFFSET {offset ?? 0} ROWS";
        var actualLimit = limit switch { null => 100, -1 => null, _ => limit };
        if (actualLimit.HasValue)
            orderBy += $" FETCH NEXT {actualLimit} ROWS ONLY";
        return orderBy;
    }

    /// <inheritdoc />
    public string ParameterPrefix => "@";

    /// <inheritdoc />
    public string LastInsertedIdentity => "SCOPE_IDENTITY()";

    /// <inheritdoc />
    public string LikePattern(string paramName, LikePatternType patternType) => patternType switch
    {
        LikePatternType.Contains => $"'%' + {paramName} + '%'",
        LikePatternType.StartsWith => $"{paramName} + '%'",
        LikePatternType.EndsWith => $"'%' + {paramName}",
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
