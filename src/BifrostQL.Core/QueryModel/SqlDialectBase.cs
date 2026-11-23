using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Utils;

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
    public abstract RawSqlValidationResult ValidateReadOnlySql(string sql);

    /// <inheritdoc />
    public abstract Task<ReadOnlySqlScope> BeginReadOnlyScopeAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken);

    /// <inheritdoc />
    public virtual string ParameterPrefix => "@";

    /// <inheritdoc />
    public virtual string LastInsertedIdentity => _lastInsertedIdentity;

    /// <inheritdoc />
    public virtual string EmptyInsertValues => " DEFAULT VALUES";

    /// <inheritdoc />
    public virtual string? ReturningIdentityClause => _returningIdentityClause;

    /// <inheritdoc />
    public virtual string? ReturningIdentityClauseFor(IReadOnlyList<string> keyColumns) => ReturningIdentityClause;

    /// <inheritdoc />
    public virtual int? OrderedInsertReturningParameterLimit => null;

    /// <inheritdoc />
    public virtual string AssignmentPlaceholder(string columnName, string? dataType)
        => CastParameterReference($"{ParameterPrefix}{SqlParameterNames.Sanitize(columnName)}", dataType);

    /// <inheritdoc />
    public virtual string CastParameterReference(string placeholder, string? dataType) => placeholder;

    /// <inheritdoc />
    public virtual string DateTimeComparisonReference(string placeholder, string? dataType) => placeholder;

    /// <inheritdoc />
    public virtual bool ComparesAsUuid(string? dataType) => false;

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
        _ => throw new BifrostExecutionError(
            $"Unknown filter operator '{op}'. A silent fallback to '=' would match the " +
            $"wrong rows. Valid operators: _eq, _neq, _lt, _lte, _gt, _gte, _contains, " +
            $"_starts_with, _ends_with, _like, _ncontains, _nstarts_with, _nends_with, " +
            $"_nlike, _in, _nin, _between, _nbetween.")
        { ErrorCode = FilterOperators.InvalidOperatorErrorCode }
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
        SqlColumnKind.KeyText => $"VARCHAR({SqlColumnDefinition.KeyTextMaxLength})",
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
    public virtual string ChildSetLockTableHint => UpdateLockTableHint;

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
    public virtual string LowerExpression(SqlExpr expr, IDbTable table, SqlParameterCollection parameters, string? tableAlias = null)
    {
        ArgumentNullException.ThrowIfNull(expr);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(parameters);

        // One closure carries the table, the parameter collection and the alias down the whole
        // tree. The date/JSON hooks recurse only through it, so no dialect can lower a nested
        // operand without the alias.
        string Lower(SqlExpr node) => node switch
        {
            SqlExpr.Col col => LowerColumn(col, table, tableAlias),
            SqlExpr.Param p => parameters.AddParameter(p.Value, p.DbType).Name,
            SqlExpr.Lit lit => parameters.AddParameter(lit.Value).Name,
            SqlExpr.Fn fn => LowerFunction(fn, Lower),
            SqlExpr.Cast cast => $"CAST({Lower(cast.Operand)} AS {RenderCastType(cast.TargetType)})",
            SqlExpr.Concat concat => LowerConcat(concat.Parts.Select(Lower).ToList()),
            SqlExpr.Case @case => LowerCase(@case, Lower),
            SqlExpr.DateAdd dateAdd => LowerDateAdd(dateAdd, Lower),
            SqlExpr.DateDiff dateDiff => LowerDateDiff(dateDiff, Lower),
            SqlExpr.DatePart datePart => LowerDatePart(datePart, Lower),
            SqlExpr.JsonGet jsonGet => LowerJsonGet(jsonGet, Lower),
            _ => throw new BifrostExecutionError(
                $"Unsupported SqlExpr node type '{node.GetType().Name}' in expression lowering.")
        };

        return Lower(expr);
    }

    private string LowerColumn(SqlExpr.Col col, IDbTable table, string? tableAlias)
    {
        if (!table.ColumnLookup.TryGetValue(col.Name, out var column))
            throw new BifrostExecutionError(
                $"Unknown column '{col.Name}' referenced in a SQL expression on table '{table.DbName}'. " +
                "An expression column reference must name a real schema column of the table.");
        var escaped = EscapeIdentifier(column.DbName);
        return string.IsNullOrWhiteSpace(tableAlias) ? escaped : $"{EscapeIdentifier(tableAlias)}.{escaped}";
    }

    private string LowerFunction(SqlExpr.Fn fn, Func<SqlExpr, string> lowerOperand)
    {
        // Resolve the dialect name FIRST so an unknown function fails fast (naming the
        // symbol) before any argument is lowered — there is no pass-through path.
        var name = MapFunctionName(fn.Name);
        var args = fn.Args.Select(lowerOperand);
        return $"{name}({string.Join(", ", args)})";
    }

    private static string LowerCase(SqlExpr.Case node, Func<SqlExpr, string> lowerOperand)
    {
        if (node.Branches.Count == 0)
            throw new BifrostExecutionError(
                "A CASE expression must have at least one WHEN branch; an empty CASE is not valid SQL.");

        var sb = new StringBuilder("CASE ");
        sb.Append(lowerOperand(node.Operand));
        foreach (var branch in node.Branches)
        {
            sb.Append(" WHEN ").Append(lowerOperand(branch.When));
            sb.Append(" THEN ").Append(lowerOperand(branch.Then));
        }
        if (node.Else is not null)
            sb.Append(" ELSE ").Append(lowerOperand(node.Else));
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

    /// <inheritdoc />
    /// <remarks>
    /// Template method: each segment is rendered by <see cref="RenderKeySegment"/> (the text
    /// <see cref="Storage.S3ObjectKeyMap"/> builds from the value the reader returns) and
    /// escaped by <see cref="EscapeKeySegment"/>; the separator is the constant <c>'/'</c>
    /// joined by <see cref="LowerConcat"/>, and the whole is wrapped by <see cref="BinaryOrder"/>.
    /// </remarks>
    public virtual string ObjectKeyExpression(IReadOnlyList<ObjectKeySegmentColumn> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
            throw new ArgumentException("An object key needs at least one primary-key segment.", nameof(segments));

        var parts = new List<string>(segments.Count * 2 - 1);
        foreach (var segment in segments)
        {
            var text = RenderKeySegment(segment);
            // A caller asks ObjectKeyRefusal before building a keyed query; reaching here
            // with an unrenderable column is a routing defect, not a listing outcome.
            if (text.Refusal != null)
                throw new NotSupportedException(text.Refusal);
            if (parts.Count > 0)
                parts.Add("'/'");
            parts.Add(EscapeKeySegment(text));
        }

        return BinaryOrder(parts.Count == 1 ? parts[0] : LowerConcat(parts));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The same <see cref="RenderKeySegment"/> call <see cref="ObjectKeyExpression"/> makes, so
    /// the answer and the rendering cannot disagree. Refusals depend on the declared column
    /// type only, never on <see cref="ObjectKeySegmentColumn.ReadClrType"/>.
    /// </remarks>
    public virtual string? ObjectKeyRefusal(IReadOnlyList<ObjectKeySegmentColumn> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        foreach (var segment in segments)
        {
            var refusal = RenderKeySegment(segment).Refusal;
            if (refusal != null)
                return refusal;
        }
        return null;
    }

    /// <summary>
    /// One primary-key column as SQL text equal to <see cref="Storage.S3ObjectKeyMap"/>'s
    /// unescaped rendering (<c>Stringify</c>) of the value the reader returns for it, with
    /// the set of characters that text can contain, or <see cref="Unrenderable"/> for a
    /// column type this engine cannot render byte-identically. Never throws for a type:
    /// <see cref="ObjectKeyRefusal"/> asks the same question.
    /// </summary>
    protected abstract KeySegmentText RenderKeySegment(ObjectKeySegmentColumn segment);

    /// <summary>
    /// Percent-encodes the UTF-8 bytes of the text expression <paramref name="textExpression"/>
    /// the way <see cref="Storage.S3ObjectKeyMap"/> escapes a segment: every byte outside
    /// <c>[A-Za-z0-9_-]</c> becomes uppercase <c>%XX</c>.
    /// </summary>
    protected abstract string PercentEncodeUtf8(string textExpression);

    /// <summary>Wraps a text expression so comparisons and ORDER BY on it use binary order.</summary>
    protected abstract string BinaryOrder(string textExpression);

    /// <summary>
    /// A rendered key segment. <see cref="ReservedChars"/> null means the text may hold any
    /// character (escaped by <see cref="PercentEncodeUtf8"/>); otherwise the text holds only
    /// unreserved bytes and these ASCII characters, escaped by one <c>REPLACE</c> each.
    /// <see cref="Refusal"/> set means the type has no exact rendering (<see cref="Unrenderable"/>).
    /// </summary>
    protected readonly record struct KeySegmentText(string Sql, string? ReservedChars, string? Refusal = null)
    {
        /// <summary>Text of any characters.</summary>
        public static KeySegmentText AnyText(string sql) => new(sql, null);

        /// <summary>Text of unreserved bytes plus <paramref name="reservedChars"/> (ASCII, no <c>%</c>).</summary>
        public static KeySegmentText Ascii(string sql, string reservedChars = "") => new(sql, reservedChars);

        /// <summary>Text already in escaped form.</summary>
        public static KeySegmentText Escaped(string sql) => new(sql, "");
    }

    /// <summary>Applies the escaping <paramref name="text"/> declares it needs.</summary>
    protected string EscapeKeySegment(KeySegmentText text) => text.ReservedChars switch
    {
        null => PercentEncodeUtf8(text.Sql),
        var chars => ReplaceReservedChars(text.Sql, chars),
    };

    /// <summary>
    /// Escapes ASCII text whose only reserved characters are <paramref name="reservedChars"/>,
    /// one nested <c>REPLACE</c> per character. Exact: no replacement output contains a
    /// character another <c>REPLACE</c> matches (<c>%</c> and hex digits are never in the set).
    /// </summary>
    protected static string ReplaceReservedChars(string textExpression, string reservedChars)
    {
        var sql = textExpression;
        foreach (var c in reservedChars)
        {
            if (c > '\u007F' || c is '%' or '\'' || IsUnreservedKeyChar(c))
                throw new ArgumentException($"'{c}' is not a reserved ASCII key character.", nameof(reservedChars));
            sql = $"REPLACE({sql}, '{c}', '%{(int)c:X2}')";
        }
        return sql;
    }

    private static bool IsUnreservedKeyChar(char c) =>
        c is '-' or '_' or (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    /// <summary>The bytes <see cref="Storage.S3ObjectKeyMap"/> leaves unescaped: <c>[A-Za-z0-9_-]</c>.</summary>
    protected static readonly byte[] UnreservedKeyBytes =
        new[] { (byte)'-', (byte)'_' }
            .Concat(Enumerable.Range('0', 10).Select(b => (byte)b))
            .Concat(Enumerable.Range('A', 26).Select(b => (byte)b))
            .Concat(Enumerable.Range('a', 26).Select(b => (byte)b))
            .ToArray();

    /// <summary>
    /// Turns <paramref name="percentEncodedBytes"/>, a text expression holding every byte
    /// as an uppercase <c>%XX</c> token, into the key escaping by replacing the token of
    /// each unreserved byte with the byte itself (nested <c>REPLACE</c>, one per unreserved
    /// byte). Exact: a <c>%</c> occurs only at a token start, so a match can never span two
    /// tokens, and a replacement never produces a <c>%</c>.
    /// </summary>
    protected static string RestoreUnreservedKeyBytes(string percentEncodedBytes)
    {
        var sql = percentEncodedBytes;
        foreach (var b in UnreservedKeyBytes)
            sql = $"REPLACE({sql}, '%{b:X2}', '{(char)b}')";
        return sql;
    }

    /// <summary>
    /// SQL predicate text true when the integer expression <paramref name="byteExpression"/>
    /// is an unreserved key byte (<c>[A-Za-z0-9_-]</c>).
    /// </summary>
    protected static string IsUnreservedKeyByte(string byteExpression) =>
        $"({byteExpression} IN (45, 95) OR {byteExpression} BETWEEN 48 AND 57 " +
        $"OR {byteExpression} BETWEEN 65 AND 90 OR {byteExpression} BETWEEN 97 AND 122)";

    /// <summary>
    /// The key column's base type name: lower-case, without a length/precision suffix or
    /// MySQL's <c>unsigned</c>/<c>zerofill</c> attributes (<c>DECIMAL(10,2) UNSIGNED</c> → <c>decimal</c>).
    /// </summary>
    protected static string KeyTypeName(ObjectKeySegmentColumn segment)
    {
        var t = StringNormalizer.NormalizeType(segment.Column.DataType);
        var paren = t.IndexOf('(');
        if (paren >= 0)
        {
            var close = t.IndexOf(')', paren);
            t = close < 0 ? t[..paren] : t[..paren] + t[(close + 1)..];
        }
        return t.Replace(" unsigned", "", StringComparison.Ordinal)
            .Replace(" zerofill", "", StringComparison.Ordinal)
            .Trim();
    }

    /// <summary>
    /// A key column type this engine cannot render byte-identically to the C# key. The
    /// refusal names the type only, never a value.
    /// </summary>
    protected static KeySegmentText Unrenderable(ObjectKeySegmentColumn segment, string? reason = null) =>
        new("", null,
            $"Primary-key type '{segment.Column.DataType}' cannot be rendered as an S3 object key in SQL on this engine"
            + (reason == null ? "." : $": {reason}."));

    /// <summary>
    /// The largest decimal scale the CLR <see cref="decimal"/> keeps. A column with a larger
    /// declared scale is read rounded, so the engine's own text of it is not the key.
    /// </summary>
    protected const int MaxClrDecimalScale = 28;

    /// <summary>
    /// A decimal column's text <paramref name="sql"/> (digits, sign and <c>.</c>), or
    /// <see cref="Unrenderable"/> when its declared scale is more than the CLR value holds.
    /// </summary>
    protected static KeySegmentText ClrDecimalText(ObjectKeySegmentColumn segment, string sql) =>
        segment.Column.NumericScale is > MaxClrDecimalScale
            ? Unrenderable(segment, $"scale above {MaxClrDecimalScale} is rounded when read")
            : KeySegmentText.Ascii(sql, ".");

    // ---- .NET float text -------------------------------------------------------------

    /// <summary>Length of a text expression, in characters (all key arithmetic text is ASCII).</summary>
    protected abstract string KeyTextLength(string text);

    /// <summary>1-based position of the single-character literal <paramref name="needle"/> in <paramref name="text"/>, 0 when absent.</summary>
    protected abstract string KeyTextPosition(char needle, string text);

    /// <summary>The substring of <paramref name="text"/> from 1-based <paramref name="start"/>, <paramref name="length"/> characters.</summary>
    protected abstract string KeyTextSubstring(string text, string start, string length);

    /// <summary>An integer expression as decimal text.</summary>
    protected abstract string KeyIntText(string integer);

    /// <summary>Decimal text (optionally signed with <c>+</c>/<c>-</c>) as an integer.</summary>
    protected abstract string KeyTextInt(string text);

    /// <summary><paramref name="text"/> without leading <c>0</c> characters.</summary>
    protected abstract string KeyTrimLeadingZeros(string text);

    /// <summary><paramref name="text"/> without trailing <c>0</c> characters.</summary>
    protected abstract string KeyTrimTrailingZeros(string text);

    /// <summary>A long run of zeros to cut padding from (a double's exponent is at most 324).</summary>
    private static readonly string Zeros = "'" + new string('0', 340) + "'";

    /// <summary>Upper bound for a "rest of the string" substring length.</summary>
    private const string RestLength = "400";

    /// <summary>
    /// Appended to each derived table of the float formatter (<see cref="DotNetFloatText"/>)
    /// to keep the engine from flattening it into its parent. Every stage names the previous
    /// stage's columns several times, so flattening the chain substitutes each one again and
    /// the compiled expression grows with the product of the references. Empty by default.
    /// </summary>
    protected virtual string DerivedTableFence => "";

    /// <summary>
    /// The text <c>double.ToString(null, InvariantCulture)</c> (or <c>float</c>'s, when
    /// <paramref name="single"/>) produces for the value whose shortest round-trip digits
    /// <paramref name="shortestText"/> holds. <paramref name="shortestText"/> is any decimal
    /// notation of those digits: <c>[-]int[.frac][e±exp]</c> in either case, trailing zeros
    /// allowed, or <c>nan</c>/<c>infinity</c>/<c>-infinity</c>.
    /// </summary>
    /// <remarks>
    /// .NET Core 3.0+ round-trip formatting: scientific when the exponent of the leading
    /// digit is at least 17 (double) or 9 (float), or below -4; the exponent has at least
    /// two digits. The expression is a chain of correlated derived tables, each naming the
    /// previous stage's values, which keeps the SQL text linear in the stage count.
    /// </remarks>
    protected string DotNetFloatText(string shortestText, bool single)
    {
        string Sub(string t, string start, string len) => KeyTextSubstring(t, start, len);
        string Cat(params string[] parts) => LowerConcat(parts);

        var s1 = $"(SELECT LOWER({shortestText}) AS t{DerivedTableFence}) AS k1";
        var s2 = "(SELECT k1.t, CASE WHEN " + Sub("k1.t", "1", "1") + " = '-' THEN 1 ELSE 0 END AS neg, "
            + "CASE WHEN " + Sub("k1.t", "1", "1") + " = '-' THEN " + Sub("k1.t", "2", RestLength) + " ELSE k1.t END AS a"
            + $" FROM {s1}{DerivedTableFence}) AS k2";
        var s3 = $"(SELECT k2.t, k2.neg, k2.a, {KeyTextPosition('e', "k2.a")} AS pe FROM {s2}{DerivedTableFence}) AS k3";
        var s4 = "(SELECT k3.t, k3.neg, "
            + "CASE WHEN k3.pe > 0 THEN " + Sub("k3.a", "1", "k3.pe - 1") + " ELSE k3.a END AS m, "
            + "CASE WHEN k3.pe > 0 THEN " + KeyTextInt(Sub("k3.a", "k3.pe + 1", RestLength)) + " ELSE 0 END AS x"
            + $" FROM {s3}{DerivedTableFence}) AS k4";
        var s5 = $"(SELECT k4.t, k4.neg, k4.m, k4.x, {KeyTextPosition('.', "k4.m")} AS pd FROM {s4}{DerivedTableFence}) AS k5";
        var s6 = "(SELECT k5.t, k5.neg, k5.x, "
            + "CASE WHEN k5.pd > 0 THEN " + Sub("k5.m", "1", "k5.pd - 1") + " ELSE k5.m END AS i, "
            + "CASE WHEN k5.pd > 0 THEN " + Sub("k5.m", "k5.pd + 1", RestLength) + " ELSE '' END AS f"
            + $" FROM {s5}{DerivedTableFence}) AS k6";
        var s7 = "(SELECT k6.t, k6.neg, k6.x, " + KeyTextLength("k6.i") + " AS li, " + Cat("k6.i", "k6.f") + " AS raw"
            + $" FROM {s6}{DerivedTableFence}) AS k7";
        var s8 = "(SELECT k7.t, k7.neg, k7.x, k7.li, " + KeyTextLength("k7.raw") + " AS lraw, "
            + KeyTrimLeadingZeros("k7.raw") + " AS nz FROM " + s7 + DerivedTableFence + ") AS k8";
        var s9 = "(SELECT k8.t, k8.neg, " + KeyTrimTrailingZeros("k8.nz") + " AS d, "
            + "k8.li - 1 - (k8.lraw - " + KeyTextLength("k8.nz") + ") + k8.x AS e FROM " + s8 + DerivedTableFence + ") AS k9";
        return DotNetFloatLayout(s9, single);
    }

    /// <summary>
    /// The .NET layout of parsed float digits. <paramref name="parsed"/> is a derived table
    /// aliased <c>k9</c> with columns <c>t</c> (the lower-case source text, matched against
    /// <c>nan</c>/<c>infinity</c>), <c>neg</c> (1 when negative), <c>d</c> (the significant
    /// digits, no leading or trailing zero; empty for zero) and <c>e</c> (the decimal
    /// exponent of the first digit).
    /// </summary>
    protected string DotNetFloatLayout(string parsed, bool single)
    {
        var sciFrom = single ? 9 : 17;
        string Sub(string t, string start, string len) => KeyTextSubstring(t, start, len);
        string Cat(params string[] parts) => LowerConcat(parts);
        var s10 = $"(SELECT k9.t, k9.neg, k9.d, k9.e, {KeyTextLength("k9.d")} AS ld FROM {parsed}{DerivedTableFence}) AS k10";

        const string d = "k10.d", e = "k10.e", ld = "k10.ld";
        var sci = Cat(
            Sub(d, "1", "1"),
            $"CASE WHEN {ld} > 1 THEN " + Cat("'.'", Sub(d, "2", RestLength)) + " ELSE '' END",
            "'E'",
            $"CASE WHEN {e} < 0 THEN '-' ELSE '+' END",
            $"CASE WHEN {e} > -10 AND {e} < 10 THEN '0' ELSE '' END",
            KeyIntText($"ABS({e})"));
        var fix = $"CASE WHEN {e} >= {ld} - 1 THEN " + Cat(d, Sub(Zeros, "1", $"{e} - {ld} + 1"))
            + $" WHEN {e} >= 0 THEN " + Cat(Sub(d, "1", $"{e} + 1"), "'.'", Sub(d, $"{e} + 2", RestLength))
            + " ELSE " + Cat("'0.'", Sub(Zeros, "1", $"-{e} - 1"), d) + " END";
        var signed = $"CASE WHEN {e} >= {sciFrom} OR {e} < -4 THEN {sci} ELSE {fix} END";

        return "(SELECT CASE"
            + " WHEN k10.t = 'nan' THEN 'NaN' WHEN k10.t = 'infinity' OR k10.t = 'inf' THEN 'Infinity'"
            + " WHEN k10.t = '-infinity' OR k10.t = '-inf' THEN '-Infinity'"
            + $" WHEN {d} = '' THEN CASE WHEN k10.neg = 1 THEN '-0' ELSE '0' END"
            + " ELSE " + Cat("CASE WHEN k10.neg = 1 THEN '-' ELSE '' END", signed)
            + $" END FROM {s10})";
    }

    /// <summary>
    /// The shortest round-trip digits of the float expression <paramref name="value"/>, for an
    /// engine whose formatter rounds correctly to a requested precision but does not pick the
    /// shortest: the first of <paramref name="fractionDigits"/> (ascending scientific
    /// precisions) whose text reads back as the same value, else the last. A double's
    /// shortest digits fit 15, 16 or 17 significant digits whenever 15 do not suffice (15
    /// digits always round-trip a decimal of 15 or fewer), so <c>14, 15, 16</c> fraction
    /// digits cover it; a float's are <c>5, 6, 7, 8</c>.
    /// </summary>
    protected static string ShortestFloatCascade(
        string value, IReadOnlyList<int> fractionDigits, Func<string, int, string> scientific, Func<string, string, string> readsBackEqual)
    {
        var sb = new StringBuilder("CASE");
        for (var i = 0; i < fractionDigits.Count - 1; i++)
        {
            var candidate = scientific(value, fractionDigits[i]);
            sb.Append(" WHEN ").Append(readsBackEqual(candidate, value)).Append(" THEN ").Append(candidate);
        }
        sb.Append(" ELSE ").Append(scientific(value, fractionDigits[^1])).Append(" END");
        return sb.ToString();
    }

    /// <summary>
    /// Maps a canonical allow-listed function name to this dialect's spelling. The allow-list
    /// (UPPER/LOWER/LEN/BYTE_LEN/COALESCE/ABS/ROUND) is closed: an unrecognized name throws a
    /// <see cref="BifrostExecutionError"/> naming the symbol rather than passing arbitrary
    /// text through as a function call. Most names are identical across engines; only
    /// LEN and BYTE_LEN differ (SQL Server overrides them), so the default maps both to the
    /// <c>LENGTH</c> used by PostgreSQL, MySQL, and SQLite, which counts bytes on a binary value.
    /// </summary>
    protected virtual string MapFunctionName(string name) => name switch
    {
        "UPPER" => "UPPER",
        "LOWER" => "LOWER",
        "LEN" => "LENGTH",
        "BYTE_LEN" => "LENGTH",
        "COALESCE" => "COALESCE",
        "ABS" => "ABS",
        "ROUND" => "ROUND",
        _ => throw new BifrostExecutionError(
            $"Unknown SQL function '{name}' in expression tree. Allowed functions: " +
            "UPPER, LOWER, LEN, BYTE_LEN, COALESCE, ABS, ROUND.")
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
    protected abstract string LowerDateAdd(SqlExpr.DateAdd node, Func<SqlExpr, string> lowerOperand);

    /// <summary>
    /// Lowers a <see cref="SqlExpr.DateDiff"/> into this dialect's native difference form.
    /// Abstract for the same reason as <see cref="LowerDateAdd"/>; an engine that cannot express a
    /// requested <see cref="DateUnit"/> exactly must throw
    /// <see cref="SqlExprLoweringNotSupportedException"/> (naming node + dialect) rather than emit a
    /// wrong approximation.
    /// </summary>
    protected abstract string LowerDateDiff(SqlExpr.DateDiff node, Func<SqlExpr, string> lowerOperand);

    /// <summary>
    /// Lowers a <see cref="SqlExpr.DatePart"/> into this dialect's native field-extraction form.
    /// Abstract for the same reason as <see cref="LowerDateAdd"/>.
    /// </summary>
    protected abstract string LowerDatePart(SqlExpr.DatePart node, Func<SqlExpr, string> lowerOperand);

    /// <summary>
    /// Lowers a <see cref="SqlExpr.JsonGet"/> into this dialect's native JSON scalar-extraction
    /// form. Abstract for the same reason as <see cref="LowerDateAdd"/>. Implementers splice the
    /// path from <see cref="SqlExpr.JsonGet.Path"/>, whose segments are already validated safe by
    /// <see cref="JsonPath"/>, and lower <see cref="SqlExpr.JsonGet.Source"/> through
    /// <c>lowerOperand</c> so its value binds as a parameter and its columns keep the table alias.
    /// </summary>
    protected abstract string LowerJsonGet(SqlExpr.JsonGet node, Func<SqlExpr, string> lowerOperand);
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
