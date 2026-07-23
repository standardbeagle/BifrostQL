using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

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
    /// No fast path by default: Postgres's reltuples and MySQL's information_schema
    /// counts are estimates that can drift far from the truth (both are refreshed
    /// by statistics jobs, not maintained per-write), so COUNT(*) stays the honest
    /// default. SQL Server's partition catalog is write-maintained and overrides this.
    /// </remarks>
    public virtual string? UnfilteredCountSql(string? schema, string tableName) => null;

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

    /// <inheritdoc />
    /// <remarks>
    /// Abstract on purpose: there is no safe cross-engine default for full-text search
    /// (see <see cref="ISqlDialect.SearchPredicate"/>). Every concrete dialect MUST supply
    /// its engine's predicate, so a dialect that forgets fails to compile rather than
    /// silently emitting a non-searching or wrong predicate.
    /// </remarks>
    public abstract ParameterizedSql SearchPredicate(FtsPredicateRequest request);

    /// <summary>
    /// Shared guard every <see cref="SearchPredicate"/> override calls first: a
    /// <c>_search</c> lowering must have at least one parsed term and at least one
    /// searchable column, or the emitted predicate would be malformed. Throws a clean
    /// <see cref="BifrostExecutionError"/> rather than letting an empty column list splice
    /// into invalid SQL.
    /// </summary>
    /// <summary>
    /// Renders one column reference for a <c>_search</c> predicate, qualified by the
    /// request's table alias when it has one.
    ///
    /// A <c>_search</c> predicate does not always sit in a single-table SELECT: it can
    /// land inside a relationship sub-query, a self-referencing join, or beside another
    /// table's columns, where a bare column name is ambiguous — MySQL's
    /// <c>MATCH(cols)</c> and SQL Server's <c>CONTAINS((cols), …)</c> require every
    /// column to resolve to the indexed table, and Postgres's <c>to_tsvector</c> over a
    /// bare column resolves against whichever relation happens to expose that name. Three
    /// of the four dialects emitted the bare name and so bound to the wrong table or
    /// failed outright; SQLite already qualified its key reference. Routing every dialect
    /// through this one helper is what keeps them from diverging again.
    /// </summary>
    protected string SearchColumnRef(FtsPredicateRequest request, string columnName)
        => string.IsNullOrEmpty(request.TableAlias)
            ? EscapeIdentifier(columnName)
            : $"{EscapeIdentifier(request.TableAlias)}.{EscapeIdentifier(columnName)}";

    protected static void RequireSearchable(FtsPredicateRequest request)
    {
        if (request.Terms.Count == 0)
            throw new BifrostExecutionError(
                "A _search predicate was requested with no query terms; provide at least one search term.");
        if (request.ColumnNames.Count == 0)
            throw new BifrostExecutionError(
                $"Table '{request.TableName}' declares no searchable columns; configure the 'search' metadata " +
                "with a comma-separated list of string columns before using _search.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// The Template-Method core: an exhaustive switch over the closed <see cref="SqlExpr"/>
    /// hierarchy. Value nodes bind into <paramref name="parameters"/> and emit only the
    /// placeholder; identifier/function symbols are validated. The dialect-specific spelling
    /// of concat, function names, and cast types is delegated to the overridable hooks
    /// <see cref="LowerConcat"/>, <see cref="MapFunctionName"/>, and <see cref="RenderCastType"/>,
    /// so no per-dialect branching lives in this method or in the node types.
    /// </remarks>
    public virtual string LowerExpression(SqlExpr expr, IDbTable table, SqlParameterCollection parameters)
    {
        ArgumentNullException.ThrowIfNull(expr);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(parameters);

        return expr switch
        {
            SqlExpr.Col col => LowerColumn(col, table),
            SqlExpr.Param p => parameters.AddParameter(p.Value, p.DbType),
            SqlExpr.Lit lit => parameters.AddParameter(lit.Value),
            SqlExpr.Fn fn => LowerFunction(fn, table, parameters),
            SqlExpr.Cast cast =>
                $"CAST({LowerExpression(cast.Operand, table, parameters)} AS {RenderCastType(cast.TargetType)})",
            SqlExpr.Concat concat =>
                LowerConcat(concat.Parts.Select(part => LowerExpression(part, table, parameters)).ToList()),
            SqlExpr.Case @case => LowerCase(@case, table, parameters),
            SqlExpr.DateAdd dateAdd => LowerDateAdd(dateAdd, table, parameters),
            SqlExpr.DateDiff dateDiff => LowerDateDiff(dateDiff, table, parameters),
            SqlExpr.DatePart datePart => LowerDatePart(datePart, table, parameters),
            SqlExpr.JsonGet jsonGet => LowerJsonGet(jsonGet, table, parameters),
            _ => throw new BifrostExecutionError(
                $"Unsupported SqlExpr node type '{expr.GetType().Name}' in expression lowering.")
        };
    }

    private string LowerColumn(SqlExpr.Col col, IDbTable table)
    {
        if (!table.ColumnLookup.TryGetValue(col.Name, out var column))
            throw new BifrostExecutionError(
                $"Unknown column '{col.Name}' referenced in a SQL expression on table '{table.DbName}'. " +
                "An expression column reference must name a real schema column of the table.");
        return EscapeIdentifier(column.DbName);
    }

    private string LowerFunction(SqlExpr.Fn fn, IDbTable table, SqlParameterCollection parameters)
    {
        // Resolve the dialect name FIRST so an unknown function fails fast (naming the
        // symbol) before any argument is lowered — there is no pass-through path.
        var name = MapFunctionName(fn.Name);
        var args = fn.Args.Select(a => LowerExpression(a, table, parameters));
        return $"{name}({string.Join(", ", args)})";
    }

    private string LowerCase(SqlExpr.Case node, IDbTable table, SqlParameterCollection parameters)
    {
        if (node.Branches.Count == 0)
            throw new BifrostExecutionError(
                "A CASE expression must have at least one WHEN branch; an empty CASE is not valid SQL.");

        var sb = new StringBuilder("CASE ");
        sb.Append(LowerExpression(node.Operand, table, parameters));
        foreach (var branch in node.Branches)
        {
            sb.Append(" WHEN ").Append(LowerExpression(branch.When, table, parameters));
            sb.Append(" THEN ").Append(LowerExpression(branch.Then, table, parameters));
        }
        if (node.Else is not null)
            sb.Append(" ELSE ").Append(LowerExpression(node.Else, table, parameters));
        sb.Append(" END");
        return sb.ToString();
    }

    /// <summary>
    /// Renders a <see cref="SqlExpr.Concat"/> from its already-lowered parts using this
    /// dialect's string-concatenation form. The default joins with the infix operator the
    /// dialect was constructed with (<c>+</c> for SQL Server, <c>||</c> for the
    /// standard-concat dialects); MySQL overrides it with <c>CONCAT(...)</c>. This is the
    /// same per-dialect concat distinction <see cref="LikePattern"/> already relies on.
    /// </summary>
    protected virtual string LowerConcat(IReadOnlyList<string> loweredParts)
        => string.Join($" {_stringConcatOperator} ", loweredParts);

    /// <summary>
    /// Maps a canonical allow-listed function name to this dialect's spelling. The allow-list
    /// (UPPER/LOWER/LEN/COALESCE/ABS/ROUND) is closed: an unrecognized name throws a
    /// <see cref="BifrostExecutionError"/> naming the symbol rather than passing arbitrary
    /// text through as a function call. Most names are identical across engines; only
    /// LEN differs (SQL Server overrides it), so the default maps LEN to the ANSI
    /// <c>LENGTH</c> used by PostgreSQL, MySQL, and SQLite.
    /// </summary>
    protected virtual string MapFunctionName(string name) => name switch
    {
        "UPPER" => "UPPER",
        "LOWER" => "LOWER",
        "LEN" => "LENGTH",
        "COALESCE" => "COALESCE",
        "ABS" => "ABS",
        "ROUND" => "ROUND",
        _ => throw new BifrostExecutionError(
            $"Unknown SQL function '{name}' in expression tree. Allowed functions: " +
            "UPPER, LOWER, LEN, COALESCE, ABS, ROUND.")
    };

    /// <summary>
    /// Renders a portable <see cref="SqlExprType"/> as this dialect's concrete CAST target
    /// type. Deliberately distinct from <see cref="RenderColumnType"/> (which produces DDL
    /// storage types): a CAST target has different valid spellings — MySQL's CAST accepts
    /// <c>CHAR</c>/<c>SIGNED</c> but not <c>TEXT</c>/<c>INTEGER</c>. The default covers the
    /// ANSI-ish dialects (PostgreSQL, SQLite); SQL Server and MySQL override it.
    /// </summary>
    protected virtual string RenderCastType(SqlExprType type) => type switch
    {
        SqlExprType.Text => "TEXT",
        SqlExprType.Int => "INTEGER",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    /// <summary>
    /// Lowers a <see cref="SqlExpr.DateAdd"/> into this dialect's native date-arithmetic form.
    /// Declared ABSTRACT ON PURPOSE — unlike <see cref="LowerConcat"/>/<see cref="MapFunctionName"/>/
    /// <see cref="RenderCastType"/> (whose ANSI-ish defaults are correct for most engines), the
    /// four engines' date/JSON facilities share NO portable spelling (SQL Server <c>DATEADD</c>,
    /// PostgreSQL interval arithmetic, MySQL <c>DATE_ADD</c>, SQLite <c>datetime()</c>). A shared
    /// default could only pick one engine's syntax — broken on the other three, or a silently-wrong
    /// no-op — so forcing every dialect to override makes "a new dialect forgot date/JSON lowering"
    /// a compile error, never a runtime data defect.
    /// </summary>
    protected abstract string LowerDateAdd(SqlExpr.DateAdd node, IDbTable table, SqlParameterCollection parameters);

    /// <summary>
    /// Lowers a <see cref="SqlExpr.DateDiff"/> into this dialect's native difference form.
    /// Abstract for the same reason as <see cref="LowerDateAdd"/>; an engine that cannot express a
    /// requested <see cref="DateUnit"/> exactly must throw
    /// <see cref="SqlExprLoweringNotSupportedException"/> (naming node + dialect) rather than emit a
    /// wrong approximation.
    /// </summary>
    protected abstract string LowerDateDiff(SqlExpr.DateDiff node, IDbTable table, SqlParameterCollection parameters);

    /// <summary>
    /// Lowers a <see cref="SqlExpr.DatePart"/> into this dialect's native field-extraction form.
    /// Abstract for the same reason as <see cref="LowerDateAdd"/>.
    /// </summary>
    protected abstract string LowerDatePart(SqlExpr.DatePart node, IDbTable table, SqlParameterCollection parameters);

    /// <summary>
    /// Lowers a <see cref="SqlExpr.JsonGet"/> into this dialect's native JSON scalar-extraction
    /// form. Abstract for the same reason as <see cref="LowerDateAdd"/>. Implementers splice the
    /// path from <see cref="SqlExpr.JsonGet.Path"/>, whose segments are already validated safe by
    /// <see cref="JsonPath"/>, and lower <see cref="SqlExpr.JsonGet.Source"/> through
    /// <see cref="LowerExpression"/> so its value binds as a parameter.
    /// </summary>
    protected abstract string LowerJsonGet(SqlExpr.JsonGet node, IDbTable table, SqlParameterCollection parameters);
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
