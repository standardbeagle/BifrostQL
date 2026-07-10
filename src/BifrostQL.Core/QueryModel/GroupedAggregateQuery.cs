using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;

namespace BifrostQL.Core.QueryModel
{
    /// <summary>
    /// One column that participates in the GROUP BY of a base-table aggregate
    /// query and is projected back as a group-key value. <see cref="Column"/> is
    /// the resolved model column; <see cref="GraphQlName"/> is the alias the
    /// projected value is read back by (identical to the column's GraphQL name).
    /// </summary>
    public sealed record AggregateGroupColumn(ColumnDto Column, string GraphQlName);

    /// <summary>
    /// One aggregate value projection: <see cref="Operation"/> applied to
    /// <see cref="Column"/>, grouped under the GraphQL op field <see cref="OpGroup"/>
    /// (e.g. <c>_sum</c>) and read back from the result set by <see cref="SqlAlias"/>.
    /// </summary>
    public sealed record AggregateValueColumn(
        AggregateOperationType Operation,
        ColumnDto Column,
        string OpGroup,
        string SqlAlias);

    /// <summary>
    /// The GROUP BY aggregate specification attached to a <see cref="GqlObjectQuery"/>
    /// for a dedicated per-table aggregate root field
    /// (<c>&lt;table&gt;Aggregate(filter, groupBy) { ... }</c>). When present it
    /// replaces the standard row SELECT with a single grouped statement:
    /// <c>SELECT groupCols, COUNT(*), op(col)... FROM t {filter} GROUP BY groupCols</c>.
    /// Transformer-derived filters (tenant isolation, soft-delete) are applied via the
    /// owning query's <see cref="GqlObjectQuery.Filter"/> before grouping, so scoped-out
    /// rows never reach the aggregate — the same fail-closed contract as row queries.
    /// </summary>
    public sealed class GroupedAggregate
    {
        public required IReadOnlyList<AggregateGroupColumn> GroupColumns { get; init; }
        public required bool IncludeCount { get; init; }
        public required IReadOnlyList<AggregateValueColumn> ValueColumns { get; init; }

        /// <summary>Result-set column alias for the COUNT(*) projection.</summary>
        public const string CountAlias = "_count";

        /// <summary>
        /// Emits the parameterized GROUP BY statement. All identifiers are
        /// dialect-escaped; no user-provided text is concatenated — group and value
        /// columns are resolved model columns, never client strings. Only the
        /// <paramref name="filter"/> contributes parameters (the WHERE predicate),
        /// so the returned statement carries exactly its parameters.
        /// </summary>
        public ParameterizedSql ToSqlParameterized(ISqlDialect dialect, string tableRef, ParameterizedSql filter)
        {
            var projections = new List<string>();
            foreach (var group in GroupColumns)
                projections.Add($"{dialect.EscapeIdentifier(group.Column.DbName)} AS {dialect.EscapeIdentifier(group.GraphQlName)}");

            if (IncludeCount)
                projections.Add($"COUNT(*) AS {dialect.EscapeIdentifier(CountAlias)}");

            foreach (var value in ValueColumns)
                projections.Add($"{RenderOperation(value.Operation)}({dialect.EscapeIdentifier(value.Column.DbName)}) AS {dialect.EscapeIdentifier(value.SqlAlias)}");

            var sql = $"SELECT {string.Join(", ", projections)} FROM {tableRef}{filter.Sql}";

            if (GroupColumns.Count > 0)
            {
                var groupBy = string.Join(", ", GroupColumns.Select(g => dialect.EscapeIdentifier(g.Column.DbName)));
                sql += $" GROUP BY {groupBy}";
            }

            return new ParameterizedSql(sql, filter.Parameters);
        }

        /// <summary>
        /// SQL function name for an aggregate operation. Rendered uppercase — the
        /// ANSI spelling every supported dialect (SqlServer/Postgres/MySQL/SQLite)
        /// accepts. COUNT is handled separately (COUNT(*), not a value column).
        /// </summary>
        private static string RenderOperation(AggregateOperationType operation) => operation switch
        {
            AggregateOperationType.Sum => "SUM",
            AggregateOperationType.Avg => "AVG",
            AggregateOperationType.Min => "MIN",
            AggregateOperationType.Max => "MAX",
            _ => throw new BifrostExecutionError($"Unsupported aggregate value operation '{operation}'."),
        };
    }
}
