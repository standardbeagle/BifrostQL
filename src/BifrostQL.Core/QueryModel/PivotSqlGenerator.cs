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

        var caseColumns = new List<string>();
        var paramCollection = new SqlParameterCollection();

        // Offset parameter naming if filter already has params
        // We use a separate collection and merge, since each CASE needs its own param
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
                var paramName = paramCollection.AddParameter(value);
                caseWhen = $"{aggFunc}(CASE WHEN {pivotCol} = {paramName} THEN {valueCol} END)";
            }

            caseColumns.Add($"{caseWhen} AS {dialect.EscapeIdentifier(alias)}");
        }

        parameters.AddRange(paramCollection.Parameters);

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
    /// <returns>Parameterized SQL that returns distinct pivot values.</returns>
    public static ParameterizedSql GenerateDistinctValuesSql(
        ISqlDialect dialect,
        string pivotColumn,
        string tableRef,
        ParameterizedSql? filter = null)
    {
        var sql = $"SELECT DISTINCT {dialect.EscapeIdentifier(pivotColumn)} FROM {tableRef}";

        if (filter != null && !string.IsNullOrEmpty(filter.Sql))
            sql += filter.Sql;

        sql += $" ORDER BY {dialect.EscapeIdentifier(pivotColumn)}";

        return new ParameterizedSql(sql, filter?.Parameters.ToList() ?? new List<SqlParameterInfo>());
    }
}
