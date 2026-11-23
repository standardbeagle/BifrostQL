namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Generates parameterized SQL for pivot queries: one portable CASE WHEN cross-tab
/// on every dialect, with each pivot value bound as a parameter.
/// </summary>
public static class PivotSqlGenerator
{
    /// <summary>
    /// The result-set alias of the pivot column at <paramref name="position"/> in the
    /// pivot-value list. Pivot columns are aliased by position, never by their label: a
    /// label is client data of any length, and PostgreSQL truncates identifiers to 63
    /// bytes, so a long label came back under another name (its cell read null) and two
    /// labels sharing a 63-byte prefix collided. The caller maps each position back to
    /// its label when it shapes the payload.
    /// </summary>
    public static string ColumnAlias(int position) => $"__pivot_{position}";

    /// <summary>
    /// Generates a cross-tab pivot query using CASE WHEN expressions, on every dialect.
    /// Each pivot value is bound as a parameter in its own CASE arm and never reaches
    /// the SQL text; each output column is aliased by position (<see cref="ColumnAlias"/>),
    /// so no label is ever an identifier. SQL Server's native PIVOT was retired because it
    /// wrote every label into its IN list as an identifier.
    /// </summary>
    /// <param name="dialect">SQL dialect for identifier escaping.</param>
    /// <param name="config">Pivot query configuration.</param>
    /// <param name="tableRef">Fully qualified table reference.</param>
    /// <param name="pivotValues">Distinct values from the pivot column.</param>
    /// <param name="filter">Optional WHERE clause filter.</param>
    /// <returns>Parameterized SQL for the cross-tab query.</returns>
    public static ParameterizedSql GeneratePivot(
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

        for (var position = 0; position < pivotValues.Count; position++)
        {
            var value = pivotValues[position];
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

            caseColumns.Add($"{caseWhen} AS {dialect.EscapeIdentifier(ColumnAlias(position))}");
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
    /// used when there are no distinct pivot values.
    /// </summary>
    private static ParameterizedSql GenerateEmptyPivot(
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
