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
}
