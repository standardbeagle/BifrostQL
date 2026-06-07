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
    /// Builds a per-parent paged connected-collection query. Each parent gets
    /// its own paged window: a per-parent row number (for offset/limit) and a
    /// per-parent total carried as a column. The window columns are computed at
    /// the join level — where the source (<paramref name="partitionColumns"/>)
    /// and child (<paramref name="orderColumns"/>) columns are directly
    /// available — then an outer SELECT filters on the row number. This keeps
    /// parent A's limit from consuming parent B's rows; a flat global LIMIT
    /// cannot do that.
    ///
    /// All four supported dialects implement the SQL:2003 window functions
    /// (ROW_NUMBER / COUNT OVER) identically, so the default suffices for every
    /// dialect; override only if a dialect needs different syntax.
    /// </summary>
    /// <param name="projection">The inner SELECT column list (source ids + child columns).</param>
    /// <param name="fromAndWhere">The FROM/JOIN/WHERE body (no SELECT, no ORDER BY/LIMIT).</param>
    /// <param name="partitionColumns">Partition-by expressions valid in the inner FROM (parent join-id columns).</param>
    /// <param name="orderColumns">ORDER BY expressions valid in the inner FROM, or null for a stable fallback.</param>
    /// <param name="rowNumberAlias">Alias for the per-parent row number column.</param>
    /// <param name="totalAlias">Alias for the per-parent total column.</param>
    /// <param name="offset">Rows to skip per parent (null → 0).</param>
    /// <param name="limit">Max rows per parent (null → 100 default, -1 → unlimited).</param>
    string ConnectedPaging(
        string projection,
        string fromAndWhere,
        IEnumerable<string> partitionColumns,
        IEnumerable<string>? orderColumns,
        string rowNumberAlias,
        string totalAlias,
        int? offset,
        int? limit)
    {
        var partition = string.Join(", ", partitionColumns);
        var order = orderColumns?.Any() == true
            ? string.Join(", ", orderColumns)
            : "(SELECT 1)";
        var rn = EscapeIdentifier(rowNumberAlias);
        var total = EscapeIdentifier(totalAlias);
        var window =
            $"SELECT {projection}, ROW_NUMBER() OVER (PARTITION BY {partition} ORDER BY {order}) AS {rn}, " +
            $"COUNT(*) OVER (PARTITION BY {partition}) AS {total} {fromAndWhere}";

        var actualLimit = limit switch { null => 100, -1 => (int?)null, _ => limit };
        var actualOffset = offset ?? 0;
        var lower = actualOffset + 1;
        var rnFilter = actualLimit.HasValue
            ? $"{rn} BETWEEN {lower} AND {actualOffset + actualLimit.Value}"
            : $"{rn} >= {lower}";
        return $"SELECT * FROM ({window}) {EscapeIdentifier("p")} WHERE {rnFilter}";
    }

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

    /// <summary>
    /// Generates a SQL clause that returns the identity value from a mutation statement
    /// using the dialect's native RETURNING syntax (e.g., SQLite's <c>RETURNING rowid AS ID</c>).
    /// Returns null if the dialect does not support RETURNING, in which case the caller
    /// should fall back to a separate <c>SELECT LastInsertedIdentity</c> statement.
    /// </summary>
    /// <returns>
    /// A SQL fragment to append to INSERT/UPSERT statements (e.g., " RETURNING rowid AS ID"),
    /// or null if not supported.
    /// </returns>
    string? ReturningIdentityClause => null;

    /// <summary>
    /// Generates an atomic upsert SQL statement that inserts a row or updates it if a
    /// conflict occurs on the specified key columns. Returns null if the dialect does not
    /// support native upsert syntax, in which case the caller should fall back to
    /// application-level insert-or-update logic.
    /// </summary>
    /// <param name="tableRef">The escaped, optionally schema-qualified table reference.</param>
    /// <param name="keyColumns">Primary key or unique constraint column names (unescaped).</param>
    /// <param name="allColumns">All column names being written (unescaped), including keys.</param>
    /// <param name="updateColumns">Non-key column names to update on conflict (unescaped).</param>
    /// <returns>
    /// A parameterized SQL string using @columnName parameters, or null if not supported.
    /// </returns>
    string? UpsertSql(string tableRef, IReadOnlyList<string> keyColumns, IReadOnlyList<string> allColumns, IReadOnlyList<string> updateColumns)
        => null;

    /// <summary>
    /// Whether this dialect supports the native `PIVOT (... FOR ... IN (...))`
    /// operator. Only SQL Server (and Oracle, not implemented) ship a native
    /// PIVOT; every other dialect uses the CASE WHEN cross-tab fallback.
    /// </summary>
    bool SupportsNativePivot => false;

    /// <summary>
    /// Whether a column of the given database type cannot be materialized directly by
    /// the provider (read as a CLR object) and must be cast to text in the SELECT.
    /// Defaults to false. PostgreSQL returns true for 'USER-DEFINED' types such as
    /// Apache AGE's graphid/agtype, which Npgsql refuses to read as object.
    /// </summary>
    /// <param name="dataType">The column's database data type (e.g. "USER-DEFINED").</param>
    bool RequiresTextCast(string dataType) => false;

    /// <summary>
    /// Whether a column of the given database type and resolved GraphQL type must be
    /// cast to text in the SELECT before GraphQL serialization.
    /// </summary>
    /// <param name="dataType">The column's database data type.</param>
    /// <param name="graphQlType">The resolved GraphQL type without nullability suffix.</param>
    bool RequiresTextCast(string dataType, string graphQlType) => RequiresTextCast(dataType);

    /// <summary>
    /// Wraps a (already-escaped) column expression in a cast to the dialect's text type.
    /// Used together with <see cref="RequiresTextCast"/> to read otherwise-unreadable
    /// columns as strings. Default uses ANSI CAST(expr AS varchar); providers can
    /// override with dialect-specific text extraction.
    /// </summary>
    string TextCast(string columnExpression) => $"CAST({columnExpression} AS varchar)";

    /// <summary>
    /// Wraps a column expression in a data-type-aware cast to text. Dialects can
    /// override this when some types need different text extraction than the default.
    /// </summary>
    string TextCast(string columnExpression, string dataType) => TextCast(columnExpression);
}
