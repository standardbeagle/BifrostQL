namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Generates parameterized SQL for pivot queries, supporting both
/// SQL Server native PIVOT syntax and a CASE WHEN cross-tab fallback.
/// </summary>
public static class PivotSqlGenerator
{
    /// <summary>
    /// Dialect-aware entry point. Routes to the dialect's native PIVOT via
    /// <see cref="ISqlDialect.BuildNativePivot"/> when it provides one (only SQL
    /// Server), otherwise emits the portable CASE WHEN cross-tab fallback. Use this
    /// in preference to the per-shape methods below so callers don't have to know
    /// which dialects ship a PIVOT operator, and so no dialect-specific SQL lives in
    /// Core.
    /// </summary>
    public static ParameterizedSql GeneratePivot(
        ISqlDialect dialect,
        PivotQueryConfig config,
        string tableRef,
        IReadOnlyList<object?> pivotValues,
        ParameterizedSql? filter = null)
    {
        if (pivotValues.Count == 0)
            return GenerateEmptyPivot(dialect, config, tableRef, filter);

        return dialect.BuildNativePivot(config, tableRef, pivotValues, filter)
            ?? GenerateCaseWhenPivot(dialect, config, tableRef, pivotValues, filter);
    }

    /// <summary>
    /// Generates a cross-tab pivot query using CASE WHEN expressions.
    /// This is the generic fallback for dialects that lack native PIVOT support.
    /// </summary>
    /// <param name="dialect">SQL dialect for identifier escaping.</param>
    /// <param name="config">Pivot query configuration.</param>
    /// <param name="tableRef">Fully qualified table reference.</param>
    /// <param name="pivotValues">Distinct values from the pivot column.</param>
    /// <param name="filter">Optional WHERE clause filter.</param>
    /// <returns>Parameterized SQL for the cross-tab query.</returns>
    public static ParameterizedSql GenerateCaseWhenPivot(
        ISqlDialect dialect,
        PivotQueryConfig config,
        string tableRef,
        IReadOnlyList<object?> pivotValues,
        ParameterizedSql? filter = null)
    {
        if (pivotValues.Count == 0)
            return GenerateEmptyPivot(dialect, config, tableRef, filter);

        var aggFunc = config.AggregateFunction.ToString().ToUpperInvariant();
        var groupByCols = string.Join(", ", config.GroupByColumns.Select(c => dialect.EscapeIdentifier(c)));
        var pivotCol = dialect.EscapeIdentifier(config.PivotColumn);
        var valueCol = dialect.EscapeIdentifier(config.ValueColumn);

        var parameters = new List<SqlParameterInfo>();
        if (filter != null)
            parameters.AddRange(filter.Parameters);

        // Each CASE needs its own bound parameter for the pivot value. They must not
        // collide with the filter's @pN placeholders, so start numbering AFTER the
        // filter's params — a fresh @p0 collection would emit a second @p0 with a
        // different value, producing wrong SQL (or a duplicate-parameter error).
        var caseColumns = new List<string>();
        var nextParamIndex = parameters.Count;

        foreach (var value in pivotValues)
        {
            var alias = value == null ? config.NullLabel : value.ToString()!;
            string caseWhen;

            if (value == null)
            {
                caseWhen = $"{aggFunc}(CASE WHEN {pivotCol} IS NULL THEN {valueCol} END)";
            }
            else
            {
                var paramName = $"@p{nextParamIndex++}";
                parameters.Add(new SqlParameterInfo(paramName, value));
                caseWhen = $"{aggFunc}(CASE WHEN {pivotCol} = {paramName} THEN {valueCol} END)";
            }

            caseColumns.Add($"{caseWhen} AS {dialect.EscapeIdentifier(alias)}");
        }

        var caseColumnsSql = string.Join(", ", caseColumns);
        var sql = $"SELECT {groupByCols}, {caseColumnsSql} FROM {tableRef}";

        if (filter != null && !string.IsNullOrEmpty(filter.Sql))
            sql += filter.Sql;

        sql += $" GROUP BY {groupByCols}";

        return new ParameterizedSql(sql, parameters);
    }

    /// <summary>
    /// Generates SQL that returns only the group-by columns with no pivot data,
    /// used when there are no distinct pivot values. Public so a dialect's
    /// <see cref="ISqlDialect.BuildNativePivot"/> can reuse the portable empty shape.
    /// </summary>
    public static ParameterizedSql GenerateEmptyPivot(
        ISqlDialect dialect,
        PivotQueryConfig config,
        string tableRef,
        ParameterizedSql? filter)
    {
        var groupByCols = string.Join(", ", config.GroupByColumns.Select(c => dialect.EscapeIdentifier(c)));
        var sql = $"SELECT {groupByCols} FROM {tableRef}";

        if (filter != null && !string.IsNullOrEmpty(filter.Sql))
            sql += filter.Sql;

        sql += $" GROUP BY {groupByCols}";

        return new ParameterizedSql(sql, filter?.Parameters.ToList() ?? new List<SqlParameterInfo>());
    }

    /// <summary>
    /// Generates SQL to retrieve the distinct values of the pivot column.
    /// </summary>
    /// <param name="dialect">SQL dialect for identifier escaping.</param>
    /// <param name="pivotColumn">The column to get distinct values from.</param>
    /// <param name="tableRef">Fully qualified table reference.</param>
    /// <param name="filter">Optional WHERE clause filter.</param>
    /// <param name="limit">
    /// Optional row cap. When set, the dialect's pagination (SQL Server OFFSET/FETCH,
    /// others LIMIT) bounds the distinct scan so a high-cardinality pivot column cannot
    /// force the whole distinct set into memory before the caller's cardinality guard
    /// runs. Pass <c>MaxPivotColumns + 1</c> so the "over the limit" case is still
    /// distinguishable. Null means no cap (backward-compatible).
    /// </param>
    /// <returns>Parameterized SQL that returns distinct pivot values.</returns>
    public static ParameterizedSql GenerateDistinctValuesSql(
        ISqlDialect dialect,
        string pivotColumn,
        string tableRef,
        ParameterizedSql? filter = null,
        int? limit = null)
    {
        var escaped = dialect.EscapeIdentifier(pivotColumn);
        var sql = $"SELECT DISTINCT {escaped} FROM {tableRef}";

        if (filter != null && !string.IsNullOrEmpty(filter.Sql))
            sql += filter.Sql;

        if (limit is null)
            // No cap: keep the plain ORDER BY (unchanged behaviour for existing callers).
            sql += $" ORDER BY {escaped}";
        else
            // Bound the scan with each dialect's own row-cap syntax (SQL Server OFFSET/FETCH,
            // others LIMIT) so a high-cardinality column cannot materialize its whole distinct set.
            sql += dialect.Pagination(new[] { escaped }, offset: null, limit: limit);

        return new ParameterizedSql(sql, filter?.Parameters.ToList() ?? new List<SqlParameterInfo>());
    }
}
