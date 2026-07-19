using BifrostQL.Core.QueryModel;

namespace BifrostQL.SqlServer;

/// <summary>
/// SQL Server dialect implementation.
/// Uses bracket identifiers ([name]), OFFSET/FETCH NEXT pagination (requires ORDER BY),
/// '+' for string concatenation, and SCOPE_IDENTITY() for last inserted identity.
/// </summary>
public sealed class SqlServerDialect : SqlDialectBase
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly SqlServerDialect Instance = new();

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
    /// <remarks>SQL Server has no unbounded TEXT alias worth using; NVARCHAR(MAX) is the modern form.</remarks>
    public override string RenderColumnType(SqlColumnKind kind) => kind switch
    {
        SqlColumnKind.Text => "NVARCHAR(MAX)",
        SqlColumnKind.Int => "INT",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
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
        var columnList = string.Join(", ", request.ColumnNames.Select(EscapeIdentifier));

        var predicates = request.Terms.Select(term =>
        {
            var phrase = "\"" + term.Text.Replace("\"", "\"\"") + "\"";
            var p = request.Parameters.AddParameter(phrase);
            return $"CONTAINS(({columnList}), {p})";
        }).ToList();

        return new ParameterizedSql(
            string.Join(" AND ", predicates),
            request.Parameters.Parameters.Skip(start).ToList());
    }
}
