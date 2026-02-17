namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Specifies the type of LIKE pattern to generate for text matching.
/// </summary>
public enum LikePatternType
{
    /// <summary>Matches text containing the value anywhere (e.g., '%value%').</summary>
    Contains,
    /// <summary>Matches text starting with the value (e.g., 'value%').</summary>
    StartsWith,
    /// <summary>Matches text ending with the value (e.g., '%value').</summary>
    EndsWith
}

/// <summary>
/// Abstracts database-specific SQL syntax for query generation.
/// Each supported database (SQL Server, PostgreSQL, MySQL, SQLite) provides its own
/// implementation to handle differences in identifier quoting, pagination, string
/// concatenation, and parameter syntax.
/// </summary>
public interface ISqlDialect
{
    /// <summary>
    /// Wraps an identifier (table name, column name) in database-specific escape characters.
    /// SQL Server uses brackets ([name]), PostgreSQL/SQLite use double quotes ("name"),
    /// and MySQL uses backticks (`name`).
    /// </summary>
    /// <param name="identifier">The raw identifier to escape.</param>
    /// <returns>The escaped identifier string.</returns>
    string EscapeIdentifier(string identifier);

    /// <summary>
    /// Generates a fully-qualified table reference with optional schema prefix.
    /// </summary>
    /// <param name="schema">The schema name, or null/empty to omit the schema prefix.</param>
    /// <param name="tableName">The table name.</param>
    /// <returns>An escaped, optionally schema-qualified table reference (e.g., [dbo].[Users]).</returns>
    string TableReference(string? schema, string tableName);

    /// <summary>
    /// Generates the ORDER BY, OFFSET, and LIMIT/FETCH clauses for pagination.
    /// SQL Server uses OFFSET/FETCH NEXT syntax (requires ORDER BY).
    /// PostgreSQL, MySQL, and SQLite use LIMIT/OFFSET syntax.
    /// A null limit defaults to 100 rows; a limit of -1 means no limit.
    /// </summary>
    /// <param name="sortColumns">Column expressions to sort by, or null for no explicit ordering.</param>
    /// <param name="offset">Number of rows to skip, or null for 0.</param>
    /// <param name="limit">Maximum rows to return. Null defaults to 100, -1 means unlimited.</param>
    /// <returns>The pagination SQL fragment to append to a SELECT statement.</returns>
    string Pagination(IEnumerable<string>? sortColumns, int? offset, int? limit);

    /// <summary>
    /// The prefix character for parameterized query parameters. All dialects use "@".
    /// </summary>
    string ParameterPrefix { get; }

    /// <summary>
    /// The SQL expression to retrieve the last inserted identity value.
    /// SQL Server: SCOPE_IDENTITY(), PostgreSQL: lastval(),
    /// MySQL: LAST_INSERT_ID(), SQLite: last_insert_rowid().
    /// </summary>
    string LastInsertedIdentity { get; }

    /// <summary>
    /// Generates a LIKE pattern expression using database-specific string concatenation.
    /// SQL Server uses '+', PostgreSQL/SQLite use '||', and MySQL uses CONCAT().
    /// </summary>
    /// <param name="paramName">The parameter name (including prefix) to wrap in the pattern.</param>
    /// <param name="patternType">The type of LIKE pattern (contains, starts with, or ends with).</param>
    /// <returns>A SQL expression for use in a LIKE clause.</returns>
    string LikePattern(string paramName, LikePatternType patternType);

    /// <summary>
    /// Maps a Directus-style filter operator string to its SQL operator equivalent.
    /// Supported operators: _eq (=), _neq (!=), _lt, _lte, _gt, _gte,
    /// _contains/_like (LIKE), _ncontains/_nlike (NOT LIKE),
    /// _in (IN), _nin (NOT IN), _between (BETWEEN), _nbetween (NOT BETWEEN).
    /// </summary>
    /// <param name="op">The Directus-style operator (e.g., "_eq", "_contains").</param>
    /// <returns>The corresponding SQL operator string.</returns>
    string GetOperator(string op);
}
