using System.Data;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Utils;

namespace BifrostQL.SqlServer;

/// <summary>
/// SQL Server dialect implementation.
/// Uses bracket identifiers ([name]), OFFSET/FETCH NEXT pagination (requires ORDER BY),
/// '+' for string concatenation, and SCOPE_IDENTITY() for last inserted identity.
/// </summary>
public sealed class SqlServerDialect : SqlDialectBase
{
    private static readonly RawSqlValidator RawSqlValidator = new(RawSqlLexicon.SqlServer);

    /// <inheritdoc />
    /// <remarks>
    /// SQL Server has no read-only transaction, and T-SQL needs no separator between
    /// statements, so after the lexical check the text is parsed with ScriptDom and must
    /// be exactly one batch holding one <c>SELECT</c> with no <c>INTO</c>.
    /// </remarks>
    public override RawSqlValidationResult ValidateReadOnlySql(string sql)
    {
        var lexical = RawSqlValidator.Validate(sql);
        if (!lexical.IsValid)
            return lexical;

        var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        if (errors.Count > 0)
            return RawSqlValidationResult.Fail("SQL could not be parsed as T-SQL.");
        if (fragment is not Microsoft.SqlServer.TransactSql.ScriptDom.TSqlScript { Batches: [{ Statements: [var statement] }] }
            || statement is not Microsoft.SqlServer.TransactSql.ScriptDom.SelectStatement { Into: null })
            return RawSqlValidationResult.Fail("Only a single SELECT statement is allowed.");
        return RawSqlValidationResult.Ok();
    }

    /// <inheritdoc />
    /// <remarks>SQL Server has no read-only transaction: the scope's guard is that it always
    /// rolls back, behind the single-SELECT parse in <see cref="ValidateReadOnlySql"/>.</remarks>
    public override async Task<ReadOnlySqlScope> BeginReadOnlyScopeAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
        => new ReadOnlySqlScope(await connection.BeginTransactionAsync(cancellationToken));

    /// <summary>Shared singleton instance.</summary>
    public static readonly SqlServerDialect Instance = new();

    // SqlClient infers datetime for a DateTime and rounds it to 1/300 s on the client; since
    // compatibility level 130 that rounded value no longer equals a datetime2 column's, and a
    // write stores it rounded. Bind DateTime as datetime2 instead (DateTimeComparisonReference
    // rounds it back for a datetime column).
    static SqlServerDialect() => DbParameterBinder.BindDateTimeAs(typeof(Microsoft.Data.SqlClient.SqlParameter), DbType.DateTime2);

    // SQL Server's OUTPUT clause must sit between the column list and the
    // VALUES clause, but the resolver appends ReturningIdentityClause after
    // VALUES (Postgres-style). Until the resolver supports per-dialect
    // placement, leave ReturningIdentityClause null so the resolver falls
    // back to the universal `INSERT ...; SELECT SCOPE_IDENTITY()` form.
    public SqlServerDialect() : base("[", "]", "+", "SCOPE_IDENTITY()", null)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// T-SQL takes row locks via table hints, which sit after the FROM table reference —
    /// not via a trailing FOR UPDATE clause (T-SQL has none outside cursors). UPDLOCK
    /// holds an update lock on the selected row until the transaction ends, so the
    /// change-history before-image read blocks a concurrent writer instead of racing it.
    /// </remarks>
    public override string UpdateLockTableHint => " WITH (UPDLOCK)";

    /// <inheritdoc />
    public override string ChildSetLockTableHint => " WITH (UPDLOCK, HOLDLOCK)";

    /// <inheritdoc />
    public override bool ComparesAsUuid(string? dataType) => StringNormalizer.NormalizeType(dataType) is "uniqueidentifier";

    /// <inheritdoc />
    public override string DateTimeComparisonReference(string placeholder, string? dataType)
    {
        var type = StringNormalizer.NormalizeType(dataType);
        return type is "datetime" or "smalldatetime" ? $"CAST({placeholder} AS {type})" : placeholder;
    }

    /// <inheritdoc />
    /// <remarks>SQL Server has no unbounded TEXT alias worth using; NVARCHAR(MAX) is the modern form.</remarks>
    public override string RenderColumnType(SqlColumnKind kind) => kind switch
    {
        SqlColumnKind.Text => "NVARCHAR(MAX)",
        SqlColumnKind.Int => "INT",
        SqlColumnKind.KeyText => $"NVARCHAR({SqlColumnDefinition.KeyTextMaxLength})",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <inheritdoc />
    /// <remarks>T-SQL spells string length <c>LEN</c> and byte length <c>DATALENGTH</c>; the
    /// <c>LENGTH</c> the base uses does not exist in SQL Server. All other allow-listed functions
    /// match the base.</remarks>
    protected override string MapFunctionName(string name) => name switch
    {
        "LEN" => "LEN",
        "BYTE_LEN" => "DATALENGTH",
        _ => base.MapFunctionName(name),
    };

    /// <inheritdoc />
    /// <remarks>SQL Server CAST targets: <c>NVARCHAR(MAX)</c> for text, <c>INT</c> for integer
    /// (the ANSI <c>TEXT</c>/<c>INTEGER</c> the base emits are not T-SQL CAST types).</remarks>
    protected override string RenderCastType(SqlExprType type) => type switch
    {
        SqlExprType.Text => "NVARCHAR(MAX)",
        SqlExprType.Int => "INT",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    /// <inheritdoc />
    /// <remarks>
    /// SQL Server lacks <c>CREATE TABLE IF NOT EXISTS</c>, so guard with an OBJECT_ID
    /// existence check. The table reference is already bracket-escaped, so it embeds
    /// safely inside the N'...' name literal (brackets, unlike quotes, need no escaping).
    /// </remarks>
    public override string CreateTableIfNotExistsSql(string tableReference, IReadOnlyList<SqlColumnDefinition> columns)
        => $"IF OBJECT_ID(N'{tableReference}', N'U') IS NULL CREATE TABLE {tableReference} ({RenderTableColumns(columns)})";

    /// <inheritdoc />
    /// <remarks>
    /// SQL Server requires ORDER BY for OFFSET/FETCH. When no sort columns are provided,
    /// ORDER BY (SELECT NULL) is used as a no-op ordering to satisfy the syntax requirement.
    /// </remarks>
    /// <inheritdoc />
    /// <remarks>
    /// Emits SQL Server's native `PIVOT (agg(value) FOR col IN (...))` operator,
    /// coalescing NULL pivot keys with `ISNULL(CAST(... AS NVARCHAR(MAX)), @p)`.
    /// Both constructs are T-SQL-specific, which is why this lives in the SqlServer
    /// dialect rather than in Core's PivotSqlGenerator. The empty-pivot-values shape
    /// is dialect-neutral and reuses PivotSqlGenerator.GenerateEmptyPivot.
    /// </remarks>
    public override ParameterizedSql? BuildNativePivot(
        PivotQueryConfig config,
        string tableRef,
        IReadOnlyList<object?> pivotValues,
        ParameterizedSql? filter = null)
    {
        if (pivotValues.Count == 0)
            return PivotSqlGenerator.GenerateEmptyPivot(this, config, tableRef, filter);

        // PIVOT matches CAST(col AS NVARCHAR(MAX)) against labels built in .NET, which
        // agree only for text: a bit casts to "1" (.NET "True"), a datetime to style 0,
        // and a uniqueidentifier to upper case (hidden only by a case-insensitive
        // collation). Any other type takes the portable CASE WHEN pivot, which binds
        // each value as a parameter.
        if (pivotValues.Any(v => v is not null and not string))
            return null;

        var aggFunc = config.AggregateFunction.ToString().ToUpperInvariant();
        var groupByCols = string.Join(", ", config.GroupByColumns.Select(EscapeIdentifier));
        var pivotCol = EscapeIdentifier(config.PivotColumn);
        var valueCol = EscapeIdentifier(config.ValueColumn);

        // Build column aliases for pivot values, replacing NULL with the null label.
        var pivotAliases = pivotValues
            .Select(v => v == null ? config.NullLabel : v.ToString()!)
            .ToList();

        var pivotColumnList = string.Join(", ", pivotAliases.Select(EscapeIdentifier));

        // Bind NullLabel as a parameter to prevent SQL injection via user-controlled
        // config values. The index is offset past the filter params so names don't collide.
        var filterParamCount = filter?.Parameters.Count ?? 0;
        var nullLabelParamName = $"@p{filterParamCount}";

        // Source subquery that coalesces NULL pivot values to the null label.
        var coalescedPivotCol = $"ISNULL(CAST({pivotCol} AS NVARCHAR(MAX)), {nullLabelParamName})";

        var sourceSql = $"SELECT {groupByCols}, {coalescedPivotCol} AS {EscapeIdentifier("__pivot_col")}, {valueCol}" +
                        $" FROM {tableRef}";

        if (filter != null && !string.IsNullOrEmpty(filter.Sql))
            sourceSql += filter.Sql;

        var sql = $"SELECT {groupByCols}, {pivotColumnList}" +
                  $" FROM ({sourceSql}) AS __src" +
                  $" PIVOT ({aggFunc}({valueCol}) FOR {EscapeIdentifier("__pivot_col")} IN ({pivotColumnList})) AS __pvt";

        var allParameters = (filter?.Parameters ?? Array.Empty<SqlParameterInfo>()).ToList();
        allParameters.Add(new SqlParameterInfo(nullLabelParamName, config.NullLabel));
        return new ParameterizedSql(sql, allParameters);
    }

    /// <inheritdoc />
    /// <remarks>
    /// sys.partitions.rows is write-maintained (it is what DBCC UPDATEUSAGE
    /// audits), so it answers an unfiltered total instantly where COUNT(*)
    /// scans the table — ~340ms warm / seconds cold per page turn on a 13M-row
    /// table. The IF-guarded fallback covers a caller whose permissions hide
    /// the catalog rows: it degrades to the true COUNT(*) rather than a false
    /// zero. The shape is CASE WHEN EXISTS(catalog) — NOT
    /// ISNULL(sum, (SELECT COUNT(*))): the optimizer computes an ISNULL
    /// scalar-subquery fallback EAGERLY, so that shape ran the full COUNT(*)
    /// on every call and the "fast" count was measurably identical to the scan
    /// it replaced (verified with STATISTICS TIME: ISNULL 2.6s, CASE 0ms).
    /// No variables either: the executor joins all of a request's statements
    /// into ONE batch, where a repeated DECLARE is a compile error.
    /// </remarks>
    public override string? UnfilteredCountSql(string? schema, string tableName)
    {
        var tableRef = TableReference(schema, tableName);
        var objectLiteral = tableRef.Replace("'", "''");
        var catalogSum = $"SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(N'{objectLiteral}') AND p.index_id IN (0, 1)";
        return $"SELECT CASE WHEN EXISTS ({catalogSum.Replace("SELECT SUM(p.rows)", "SELECT 1")}) " +
               $"THEN ({catalogSum}) ELSE (SELECT COUNT(*) FROM {tableRef}) END";
    }

    public override string Pagination(IEnumerable<string>? sortColumns, int? offset, int? limit)
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
    /// <remarks>
    /// T-SQL LIKE additionally treats <c>[</c> as the start of a character
    /// class, so it must be escaped along with the standard metacharacters.
    /// (<c>]</c> is only special inside a class and needs no escaping.)
    /// </remarks>
    public override string EscapeLikeValue(string value) =>
        base.EscapeLikeValue(value).Replace("[", "\\[");

    /// <summary>
    /// Sixteen rows, cross-joined four times for up to 65,536 byte positions: more than the
    /// UTF-8 length of any value SQL Server can index as a key (1,700 key bytes, at most
    /// three UTF-8 bytes per stored byte). <c>TOP</c> stops the join at the value's length.
    /// </summary>
    private const string SixteenRows =
        "(VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12),(13),(14),(15))";

    /// <inheritdoc />
    /// <remarks>
    /// Text: the value is converted to UTF-8 bytes through a <c>_UTF8</c> collation
    /// (SQL Server 2019+), walked byte by byte, and each byte emitted as itself or as
    /// <c>%XX</c>, aggregated in order with <c>STRING_AGG</c>.
    /// </remarks>
    protected override string ObjectKeySegment(string columnExpression, ObjectKeySegmentKind kind) => kind switch
    {
        ObjectKeySegmentKind.Integer => $"CONVERT(varchar(20), {columnExpression})",
        ObjectKeySegmentKind.Uuid => $"LOWER(CONVERT(char(36), {columnExpression}))",
        ObjectKeySegmentKind.Text =>
            "(SELECT STRING_AGG(CONVERT(varchar(max), CASE WHEN " + IsUnreservedKeyByte("ob.v") +
            " THEN CHAR(ob.v) ELSE '%' + CONVERT(char(2), CONVERT(binary(1), ob.v), 2) END), '')" +
            " WITHIN GROUP (ORDER BY op.i)" +
            $" FROM (SELECT CONVERT(varbinary(max), CONVERT(varchar(max), CONVERT(nvarchar(max), {columnExpression})" +
            " COLLATE Latin1_General_100_BIN2_UTF8)) AS bytes) AS ou" +
            " CROSS APPLY (SELECT TOP (DATALENGTH(ou.bytes)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i" +
            $" FROM {SixteenRows} AS d1(x) CROSS JOIN {SixteenRows} AS d2(x)" +
            $" CROSS JOIN {SixteenRows} AS d3(x) CROSS JOIN {SixteenRows} AS d4(x)) AS op" +
            " CROSS APPLY (SELECT CONVERT(int, SUBSTRING(ou.bytes, op.i, 1)) AS v) AS ob)",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <inheritdoc />
    /// <remarks>
    /// <c>_BIN2</c> compares UTF-16 code units, which differs from UTF-8 byte order only
    /// between supplementary characters and U+E000–U+FFFF. Object-key text is ASCII, so
    /// the two orders agree on every key.
    /// </remarks>
    protected override string BinaryOrder(string textExpression)
        => $"({textExpression}) COLLATE Latin1_General_100_BIN2";

    /// <summary>SQL Server's <c>DATEADD</c>/<c>DATEDIFF</c>/<c>DATEPART</c> datepart keyword.</summary>
    private static string DatePartKeyword(DateUnit unit) => unit switch
    {
        DateUnit.Year => "year",
        DateUnit.Month => "month",
        DateUnit.Day => "day",
        DateUnit.Hour => "hour",
        DateUnit.Minute => "minute",
        DateUnit.Second => "second",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null)
    };

    /// <inheritdoc />
    /// <remarks><c>DATEADD(day, &lt;amount&gt;, &lt;source&gt;)</c>. The amount lowers to a bound
    /// parameter; the datepart is a T-SQL keyword, never client text.</remarks>
    protected override string LowerDateAdd(SqlExpr.DateAdd node, Func<SqlExpr, string> lowerOperand)
    {
        var amount = lowerOperand(node.Amount);
        var source = lowerOperand(node.Source);
        return $"DATEADD({DatePartKeyword(node.Unit)}, {amount}, {source})";
    }

    /// <inheritdoc />
    /// <remarks><c>DATEDIFF(day, &lt;start&gt;, &lt;end&gt;)</c> — native for every unit, including
    /// whole months/years.</remarks>
    protected override string LowerDateDiff(SqlExpr.DateDiff node, Func<SqlExpr, string> lowerOperand)
    {
        var start = lowerOperand(node.Start);
        var end = lowerOperand(node.End);
        return $"DATEDIFF({DatePartKeyword(node.Unit)}, {start}, {end})";
    }

    /// <inheritdoc />
    /// <remarks><c>DATEPART(year, &lt;source&gt;)</c>.</remarks>
    protected override string LowerDatePart(SqlExpr.DatePart node, Func<SqlExpr, string> lowerOperand)
    {
        var source = lowerOperand(node.Source);
        return $"DATEPART({DatePartKeyword(node.Unit)}, {source})";
    }

    /// <inheritdoc />
    /// <remarks><c>JSON_VALUE(&lt;source&gt;, '$.a.b')</c>. The path is spliced from
    /// <see cref="JsonPath"/>'s validated segments, so the single-quoted path literal cannot be
    /// broken out of.</remarks>
    protected override string LowerJsonGet(SqlExpr.JsonGet node, Func<SqlExpr, string> lowerOperand)
    {
        var source = lowerOperand(node.Source);
        return $"JSON_VALUE({source}, '{node.Path.ToDollarPath()}')";
    }

    /// <inheritdoc />
    /// <remarks>
    /// SQL Server full-text search uses <c>CONTAINS((col1, col2), '&lt;condition&gt;')</c>
    /// against a full-text index/catalog on the searchable columns (the prerequisite the
    /// FTS guide documents). Each parsed term is bound as a quoted phrase — the CONTAINS
    /// search-condition grammar (AND/OR/NEAR/quotes) is injectable, so binding the term as
    /// a <c>"…"</c> phrase (internal quotes doubled) neutralizes it; a single word inside
    /// the quotes matches that word. Terms are ANDed at the SQL level to honor the pinned
    /// multi-term AND semantic uniformly rather than relying on CONTAINS's own operators.
    /// CONTAINS is case-insensitive.
    /// </remarks>
    public override ParameterizedSql SearchPredicate(FtsPredicateRequest request)
    {
        RequireSearchable(request);
        var start = request.Parameters.Parameters.Count();
        // Alias-qualified so the predicate resolves to the indexed table when it is
        // rendered beside another (a relationship sub-query, a self-join).
        var columnList = string.Join(", ", request.ColumnNames.Select(c => SearchColumnRef(request, c)));

        var predicates = request.Terms.Select(term =>
        {
            var phrase = "\"" + term.Text.Replace("\"", "\"\"") + "\"";
            var p = request.Parameters.AddParameter(phrase).Name;
            return $"CONTAINS(({columnList}), {p})";
        }).ToList();

        return new ParameterizedSql(
            string.Join(" AND ", predicates),
            request.Parameters.Parameters.Skip(start).ToList());
    }
}
