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
    public virtual string? ReturningIdentityClauseFor(IReadOnlyList<string> keyColumns) => ReturningIdentityClause;

    /// <inheritdoc />
    public virtual string AssignmentPlaceholder(string columnName, string? dataType)
        => CastParameterReference($"{ParameterPrefix}{SqlParameterNames.Sanitize(columnName)}", dataType);

    /// <inheritdoc />
    public virtual string CastParameterReference(string placeholder, string? dataType) => placeholder;

    /// <inheritdoc />
    /// <remarks>
    /// Any occurrence of the closing delimiter inside <paramref name="identifier"/>
    /// is doubled so it cannot break out of the delimited identifier context:
    /// SQL Server <c>]</c> → <c>]]</c>; double-quote dialects <c>"</c> → <c>""</c>.
    /// Normal identifiers (no delimiter character) are unchanged.
    /// </remarks>
    public virtual string EscapeIdentifier(string identifier)
    {
        var escapedInner = identifier.Replace(_identifierSuffix, _identifierSuffix + _identifierSuffix);
        return $"{_identifierPrefix}{escapedInner}{_identifierSuffix}";
    }

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

        var actualOffset = offset ?? 0;
        var actualLimit = limit switch { null => 100, -1 => null, _ => limit };

        if (actualLimit.HasValue)
        {
            result += $" LIMIT {actualLimit}";
        }
        else if (actualOffset > 0)
        {
            // No-limit sentinel (-1) combined with an offset. MySQL and SQLite
            // reject a bare `OFFSET n` that has no preceding LIMIT — a syntax
            // error. Emit an effectively-unlimited LIMIT so the OFFSET is valid.
            // long.MaxValue works as an "all remaining rows" count on MySQL,
            // SQLite, and PostgreSQL alike: it stays within PostgreSQL's signed
            // bigint LIMIT range (the MySQL 2^64-1 idiom would overflow that),
            // and PostgreSQL — which also accepts a bare OFFSET — treats the
            // large LIMIT as a harmless no-op superset.
            result += $" LIMIT {NoLimitRowCount}";
        }

        if (actualOffset > 0)
            result += $" OFFSET {actualOffset}";

        return result;
    }

    /// <summary>
    /// Row count used as an "unlimited" LIMIT when the no-limit sentinel (-1) is
    /// paired with a non-zero offset (LIMIT/OFFSET dialects require a LIMIT before
    /// OFFSET). Valid and effectively-infinite on MySQL, SQLite, and PostgreSQL.
    /// </summary>
    protected virtual string NoLimitRowCount => "9223372036854775807";

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
    public virtual string EscapeLikeValue(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <inheritdoc />
    public virtual string LikeEscapeClause => " ESCAPE '\\'";

    /// <inheritdoc />
    public virtual string GetOperator(string op) => op switch
    {
        // A join ON clause carries an empty/null operator to mean plain equality.
        null or "" => "=",
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
        _ => throw new ArgumentException(
            $"Unknown filter operator '{op}'. A silent fallback to '=' would match the " +
            $"wrong rows. Valid operators: _eq, _neq, _lt, _lte, _gt, _gte, _contains, " +
            $"_starts_with, _ends_with, _like, _ncontains, _nstarts_with, _nends_with, " +
            $"_nlike, _in, _nin, _between, _nbetween.", nameof(op))
    };

    /// <inheritdoc />
    /// <summary>
    /// Maps a portable <see cref="SqlColumnKind"/> to this dialect's concrete storage
    /// type. Overridden where the ANSI-ish default (TEXT/INTEGER) is wrong (SQL Server).
    /// </summary>
    public virtual string RenderColumnType(SqlColumnKind kind) => kind switch
    {
        SqlColumnKind.Text => "TEXT",
        SqlColumnKind.Int => "INTEGER",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Renders the escaped column list + PRIMARY KEY clause shared by every CREATE TABLE variant.</summary>
    protected string RenderTableColumns(IReadOnlyList<SqlColumnDefinition> columns)
    {
        var cols = string.Join(", ", columns.Select(c =>
            $"{EscapeIdentifier(c.Name)} {RenderColumnType(c.Kind)} {(c.Nullable ? "NULL" : "NOT NULL")}"));
        var keys = columns.Where(c => c.PrimaryKey).Select(c => EscapeIdentifier(c.Name)).ToList();
        return keys.Count > 0 ? $"{cols}, PRIMARY KEY ({string.Join(", ", keys)})" : cols;
    }

    /// <inheritdoc />
    public virtual string CreateTableIfNotExistsSql(string tableReference, IReadOnlyList<SqlColumnDefinition> columns)
        => $"CREATE TABLE IF NOT EXISTS {tableReference} ({RenderTableColumns(columns)})";

    public virtual string? UpsertSql(string tableRef, IReadOnlyList<string> keyColumns, IReadOnlyList<string> allColumns, IReadOnlyList<string> updateColumns)
        => null;

    /// <inheritdoc />
    public virtual ParameterizedSql? BuildNativePivot(
        PivotQueryConfig config,
        string tableRef,
        IReadOnlyList<object?> pivotValues,
        ParameterizedSql? filter = null)
        => null;

    /// <inheritdoc />
    public virtual bool RequiresTextCast(string dataType) => false;

    /// <inheritdoc />
    public virtual bool RequiresTextCast(string dataType, string graphQlType) => RequiresTextCast(dataType);

    /// <inheritdoc />
    public virtual string TextCast(string columnExpression) => $"CAST({columnExpression} AS varchar)";

    /// <inheritdoc />
    public virtual string TextCast(string columnExpression, string dataType) => TextCast(columnExpression);

    /// <inheritdoc />
    public virtual string UpdateLockTableHint => "";

    /// <inheritdoc />
    public virtual string UpdateLockClause => "";

    /// <inheritdoc />
    /// <remarks>SQL Server requires <c>BEGIN TRANSACTION</c>; LIMIT/OFFSET dialects override with <c>BEGIN</c>.</remarks>
    public virtual string BeginTransactionSql => "BEGIN TRANSACTION;";

    /// <inheritdoc />
    public virtual string CommitTransactionSql => "COMMIT;";

    /// <inheritdoc />
    public virtual string RollbackTransactionSql => "ROLLBACK;";
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

    /// <summary>
    /// PostgreSQL, MySQL, and SQLite all open a transaction with the bare
    /// <c>BEGIN;</c> keyword (no <c>TRANSACTION</c> noise word required), unlike
    /// SQL Server's <c>BEGIN TRANSACTION;</c>.
    /// </summary>
    public override string BeginTransactionSql => "BEGIN;";
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
