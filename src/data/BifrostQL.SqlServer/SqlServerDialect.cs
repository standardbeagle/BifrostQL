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

    /// <inheritdoc />
    /// <remarks>
    /// SQL Server requires ORDER BY for OFFSET/FETCH. When no sort columns are provided,
    /// ORDER BY (SELECT NULL) is used as a no-op ordering to satisfy the syntax requirement.
    /// </remarks>
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
    /// Sixteen rows, cross-joined four times for up to 65,536 character positions: more
    /// than the length of any value SQL Server can index as a key (1,700 key bytes).
    /// <c>TOP</c> stops the join at the value's length.
    /// </summary>
    private const string SixteenRows =
        "(VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12),(13),(14),(15))";

    /// <inheritdoc />
    /// <remarks>
    /// By declared type, as SqlClient returns the value: integers and decimals by their
    /// text (decimal keeps its scale, money its four places), bit as
    /// <c>True</c>/<c>False</c>, date/time types as <c>DateTime</c>'s and
    /// <c>DateTimeOffset</c>'s round-trip form, <c>time</c> as <c>TimeSpan</c>'s constant
    /// form, uniqueidentifier in lower case, binary as base64 (<c>FOR XML</c>'s
    /// encoding). <c>float</c> and <c>real</c> are refused: .NET writes the shortest
    /// round-trip digits, and neither <c>FORMAT</c> nor <c>CONVERT</c> rounds correctly
    /// past 15 digits (and text-to-<c>real</c> rounds twice), so no SQL text matches it on
    /// every value.
    /// </remarks>
    protected override KeySegmentText RenderKeySegment(ObjectKeySegmentColumn segment)
    {
        var x = segment.ColumnExpression;
        switch (KeyTypeName(segment))
        {
            case "tinyint" or "smallint" or "int" or "bigint":
                return KeySegmentText.Ascii($"CONVERT(varchar(20), {x})");
            case "bit":
                return KeySegmentText.Ascii($"CASE WHEN {x} = 1 THEN 'True' ELSE 'False' END");
            case "decimal" or "numeric":
                return ClrDecimalText(segment, $"CONVERT(varchar(50), {x})");
            case "money" or "smallmoney":
                return KeySegmentText.Ascii($"CONVERT(varchar(30), {x}, 2)", ".");
            case "float" or "real":
                return Unrenderable(segment, "SQL Server has no correctly rounded shortest float text");
            case "date" or "datetime" or "datetime2" or "smalldatetime":
                return KeySegmentText.Ascii(DateTimeText($"CONVERT(datetime2(7), {x})"), ":.");
            case "datetimeoffset":
                return KeySegmentText.Ascii(DateTimeText($"CONVERT(datetime2(7), {x})") + $" + DATENAME(tz, {x})", ":.+");
            case "time":
                return KeySegmentText.Ascii(
                    $"CONVERT(char(8), {x}, 108) + CASE WHEN DATEPART(nanosecond, {x}) = 0 THEN '' ELSE '.' + "
                    + Fraction7(x) + " END", ":.");
            case "uniqueidentifier":
                return KeySegmentText.Ascii($"LOWER(CONVERT(char(36), {x}))");
            case "char" or "nchar" or "varchar" or "nvarchar" or "sysname":
                return KeySegmentText.AnyText(x);
            case "binary" or "varbinary" or "timestamp" or "rowversion":
                return KeySegmentText.Ascii($"ISNULL((SELECT {x} FOR XML PATH('')), '')", "+/=");
            default:
                return Unrenderable(segment);
        }
    }

    /// <summary>A <c>datetime2(7)</c> as <c>DateTime.ToString("O")</c> of an unspecified-kind value.</summary>
    private static string DateTimeText(string datetime2) =>
        $"CONVERT(char(19), {datetime2}, 126) + '.' + {Fraction7(datetime2)}";

    /// <summary>The seven-digit fraction of a second (100 ns ticks), zero-padded.</summary>
    private static string Fraction7(string x) =>
        $"RIGHT('000000' + CONVERT(varchar(7), DATEPART(nanosecond, {x}) / 100), 7)";

    private static string Hex2(string byteValue) => $"CONVERT(char(2), CONVERT(binary(1), {byteValue}), 2)";

    /// <inheritdoc />
    /// <remarks>
    /// Walks the value's UTF-16 code units (a non-supplementary binary collation, so a
    /// surrogate is one position) and writes each code point's UTF-8 bytes from
    /// <c>UNICODE()</c> arithmetic, concatenated in order with <c>FOR XML PATH('')</c>.
    /// No <c>_UTF8</c> collation (2019) or <c>STRING_AGG</c> (2017), so neither version is a floor.
    /// The output is <c>[A-Za-z0-9_%-]</c>, which XML leaves as is.
    /// </remarks>
    protected override string PercentEncodeUtf8(string textExpression)
    {
        const string u = "oc.u", cp = "oq.cp";
        var piece = "CASE"
            + $" WHEN {u} < 128 THEN CASE WHEN {IsUnreservedKeyByte(u)} THEN CHAR({u}) ELSE '%' + {Hex2(u)} END"
            + $" WHEN {u} < 2048 THEN '%' + {Hex2($"192 + {u} / 64")} + '%' + {Hex2($"128 + {u} % 64")}"
            + $" WHEN {u} BETWEEN 55296 AND 56319 THEN '%' + {Hex2($"240 + {cp} / 262144")} + '%' + {Hex2($"128 + {cp} / 4096 % 64")}"
            + $" + '%' + {Hex2($"128 + {cp} / 64 % 64")} + '%' + {Hex2($"128 + {cp} % 64")}"
            + $" WHEN {u} BETWEEN 56320 AND 57343 THEN ''"
            + $" ELSE '%' + {Hex2($"224 + {u} / 4096")} + '%' + {Hex2($"128 + {u} / 64 % 64")} + '%' + {Hex2($"128 + {u} % 64")} END";
        return "ISNULL(CONVERT(varchar(max), (SELECT " + piece
            + $" FROM (SELECT CONVERT(nvarchar(max), {textExpression}) COLLATE Latin1_General_100_BIN2 AS s) AS ou"
            + " CROSS APPLY (SELECT TOP (DATALENGTH(ou.s) / 2) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i"
            + $" FROM {SixteenRows} AS d1(x) CROSS JOIN {SixteenRows} AS d2(x)"
            + $" CROSS JOIN {SixteenRows} AS d3(x) CROSS JOIN {SixteenRows} AS d4(x)) AS op"
            + " CROSS APPLY (SELECT UNICODE(SUBSTRING(ou.s, op.i, 1)) AS u, UNICODE(SUBSTRING(ou.s, op.i + 1, 1)) AS w) AS oc"
            + " CROSS APPLY (SELECT 65536 + (oc.u - 55296) * 1024 + (ISNULL(oc.w, 56320) - 56320) AS cp) AS oq"
            + " ORDER BY op.i FOR XML PATH(''))), '')";
    }

    /// <inheritdoc />
    protected override string KeyTextLength(string text) => $"LEN({text})";

    /// <inheritdoc />
    protected override string KeyTextPosition(char needle, string text) => $"CHARINDEX('{needle}', {text})";

    /// <inheritdoc />
    protected override string KeyTextSubstring(string text, string start, string length) => $"SUBSTRING({text}, {start}, {length})";

    /// <inheritdoc />
    protected override string KeyIntText(string integer) => $"CONVERT(varchar(12), {integer})";

    /// <inheritdoc />
    protected override string KeyTextInt(string text) => $"CONVERT(int, {text})";

    /// <inheritdoc />
    protected override string KeyTrimLeadingZeros(string text) =>
        $"CASE WHEN PATINDEX('%[^0]%', {text}) = 0 THEN '' ELSE SUBSTRING({text}, PATINDEX('%[^0]%', {text}), 400) END";

    /// <inheritdoc />
    protected override string KeyTrimTrailingZeros(string text) =>
        $"CASE WHEN PATINDEX('%[^0]%', {text}) = 0 THEN '' ELSE LEFT({text}, LEN({text}) - PATINDEX('%[^0]%', REVERSE({text})) + 1) END";

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
