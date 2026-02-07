namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Supported aggregate functions for pivot queries.
/// </summary>
public enum PivotAggregateFunction
{
    Count,
    Sum,
    Avg,
    Min,
    Max,
}

/// <summary>
/// Configuration for a pivot query that transforms rows into columns
/// based on distinct values of a pivot column.
/// </summary>
public sealed class PivotQueryConfig
{
    /// <summary>
    /// The column whose distinct values become output columns.
    /// </summary>
    public string PivotColumn { get; }

    /// <summary>
    /// The column to aggregate. When using COUNT, this may be the same as PivotColumn.
    /// </summary>
    public string ValueColumn { get; }

    /// <summary>
    /// The aggregate function to apply (COUNT, SUM, AVG, MIN, MAX).
    /// </summary>
    public PivotAggregateFunction AggregateFunction { get; }

    /// <summary>
    /// Columns to group the results by. At least one group-by column is required.
    /// </summary>
    public IReadOnlyList<string> GroupByColumns { get; }

    /// <summary>
    /// The label to use for the output column generated from NULL pivot values.
    /// Defaults to "_null_".
    /// </summary>
    public string NullLabel { get; }

    private PivotQueryConfig(
        string pivotColumn,
        string valueColumn,
        PivotAggregateFunction aggregateFunction,
        IReadOnlyList<string> groupByColumns,
        string nullLabel)
    {
        PivotColumn = pivotColumn;
        ValueColumn = valueColumn;
        AggregateFunction = aggregateFunction;
        GroupByColumns = groupByColumns;
        NullLabel = nullLabel;
    }

    /// <summary>
    /// Creates a validated PivotQueryConfig.
    /// </summary>
    /// <param name="pivotColumn">Column whose values become output columns.</param>
    /// <param name="valueColumn">Column to aggregate.</param>
    /// <param name="aggregateFunction">Aggregate function name (COUNT, SUM, AVG, MIN, MAX).</param>
    /// <param name="groupByColumns">Columns to group by.</param>
    /// <param name="nullLabel">Label for NULL pivot values. Defaults to "_null_".</param>
    /// <exception cref="ArgumentException">Thrown when validation fails.</exception>
    public static PivotQueryConfig Create(
        string pivotColumn,
        string valueColumn,
        string aggregateFunction,
        IReadOnlyList<string> groupByColumns,
        string? nullLabel = null)
    {
        if (string.IsNullOrWhiteSpace(pivotColumn))
            throw new ArgumentException("Pivot column is required.", nameof(pivotColumn));
        if (string.IsNullOrWhiteSpace(valueColumn))
            throw new ArgumentException("Value column is required.", nameof(valueColumn));
        if (string.IsNullOrWhiteSpace(aggregateFunction))
            throw new ArgumentException("Aggregate function is required.", nameof(aggregateFunction));
        if (groupByColumns == null || groupByColumns.Count == 0)
            throw new ArgumentException("At least one group-by column is required.", nameof(groupByColumns));

        if (!Enum.TryParse<PivotAggregateFunction>(aggregateFunction, ignoreCase: true, out var parsedFunction))
            throw new ArgumentException(
                $"Invalid aggregate function '{aggregateFunction}'. Valid values: {string.Join(", ", Enum.GetNames<PivotAggregateFunction>())}.",
                nameof(aggregateFunction));

        foreach (var col in groupByColumns)
        {
            if (string.IsNullOrWhiteSpace(col))
                throw new ArgumentException("Group-by column names must not be empty.", nameof(groupByColumns));
        }

        if (groupByColumns.Contains(pivotColumn, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Pivot column '{pivotColumn}' must not appear in group-by columns.",
                nameof(groupByColumns));

        return new PivotQueryConfig(
            pivotColumn,
            valueColumn,
            parsedFunction,
            groupByColumns,
            nullLabel ?? "_null_");
    }

    /// <summary>
    /// Validates that all referenced columns exist on the table.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when a column is not found.</exception>
    public void ValidateColumns(IDictionary<string, Model.ColumnDto> columnLookup)
    {
        if (!columnLookup.ContainsKey(PivotColumn))
            throw new ArgumentException($"Pivot column '{PivotColumn}' does not exist on the table.");
        if (!columnLookup.ContainsKey(ValueColumn))
            throw new ArgumentException($"Value column '{ValueColumn}' does not exist on the table.");
        foreach (var col in GroupByColumns)
        {
            if (!columnLookup.ContainsKey(col))
                throw new ArgumentException($"Group-by column '{col}' does not exist on the table.");
        }
    }
}
