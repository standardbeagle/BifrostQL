namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Base class for SQL dialect implementations.
/// Provides common functionality and reduces boilerplate for dialect-specific implementations.
/// </summary>
public abstract class SqlDialectBase : ISqlDialect
{
    private readonly string _identifierPrefix;
    private readonly string _identifierSuffix;
    private readonly string _stringConcatOperator;
    private readonly string _lastInsertedIdentity;
    private readonly string? _returningIdentityClause;

    protected SqlDialectBase(
        char identifierQuote,
        string stringConcatOperator,
        string lastInsertedIdentity,
        string? returningIdentityClause = null)
        : this(identifierQuote.ToString(), identifierQuote.ToString(), stringConcatOperator, lastInsertedIdentity, returningIdentityClause)
    {
    }

    protected SqlDialectBase(
        string identifierPrefix,
        string identifierSuffix,
        string stringConcatOperator,
        string lastInsertedIdentity,
        string? returningIdentityClause = null)
    {
        _identifierPrefix = identifierPrefix;
        _identifierSuffix = identifierSuffix;
        _stringConcatOperator = stringConcatOperator;
        _lastInsertedIdentity = lastInsertedIdentity;
        _returningIdentityClause = returningIdentityClause;
    }

    /// <inheritdoc />
    public virtual string ParameterPrefix => "@";

    /// <inheritdoc />
    public virtual string LastInsertedIdentity => _lastInsertedIdentity;

    /// <inheritdoc />
    public virtual string? ReturningIdentityClause => _returningIdentityClause;

    /// <inheritdoc />
    public virtual string EscapeIdentifier(string identifier) => $"{_identifierPrefix}{identifier}{_identifierSuffix}";

    /// <inheritdoc />
    public virtual string TableReference(string? schema, string tableName) =>
        string.IsNullOrWhiteSpace(schema) 
            ? EscapeIdentifier(tableName) 
            : $"{EscapeIdentifier(schema)}.{EscapeIdentifier(tableName)}";

    /// <inheritdoc />
    /// <remarks>
    /// Default implementation uses LIMIT/OFFSET syntax (PostgreSQL, MySQL, SQLite style).
    /// Override for SQL Server's OFFSET/FETCH syntax.
    /// </remarks>
    public virtual string Pagination(IEnumerable<string>? sortColumns, int? offset, int? limit)
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
    /// <remarks>
    /// Default implementation uses || operator (PostgreSQL, SQLite style).
    /// Override for SQL Server (+) or MySQL (CONCAT).
    /// </remarks>
    public virtual string LikePattern(string paramName, LikePatternType patternType)
    {
        var prefix = patternType is LikePatternType.Contains or LikePatternType.EndsWith ? "'%'" : "";
        var suffix = patternType is LikePatternType.Contains or LikePatternType.StartsWith ? "'%'" : "";
        
        if (string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(suffix))
            return paramName;

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(prefix))
            parts.Add(prefix);
        parts.Add(paramName);
        if (!string.IsNullOrEmpty(suffix))
            parts.Add(suffix);

        return string.Join($" {_stringConcatOperator} ", parts);
    }

    /// <inheritdoc />
    public virtual string GetOperator(string op) => op switch
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

    /// <inheritdoc />
    public virtual string? UpsertSql(string tableRef, IReadOnlyList<string> keyColumns, IReadOnlyList<string> allColumns, IReadOnlyList<string> updateColumns)
        => null;
}

/// <summary>
/// Base class for dialects using LIMIT/OFFSET pagination (PostgreSQL, MySQL, SQLite).
/// </summary>
public abstract class LimitOffsetDialectBase : SqlDialectBase
{
    protected LimitOffsetDialectBase(
        char identifierQuote,
        string stringConcatOperator,
        string lastInsertedIdentity,
        string? returningIdentityClause = null)
        : base(identifierQuote, stringConcatOperator, lastInsertedIdentity, returningIdentityClause)
    {
    }
}

/// <summary>
/// Base class for dialects using standard || string concatenation (PostgreSQL, SQLite).
/// </summary>
public abstract class StandardConcatDialectBase : LimitOffsetDialectBase
{
    protected StandardConcatDialectBase(
        char identifierQuote,
        string lastInsertedIdentity,
        string? returningIdentityClause = null)
        : base(identifierQuote, "||", lastInsertedIdentity, returningIdentityClause)
    {
    }
}
