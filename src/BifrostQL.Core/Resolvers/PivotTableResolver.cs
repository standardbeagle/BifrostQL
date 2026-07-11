using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Schema;
using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// The resolved, validated inputs for one pivot request. Column arguments are
    /// captured as both the raw DB name (for SQL generation) and the GraphQL name
    /// (for the client-facing payload keys); the client-supplied <see cref="Filter"/>
    /// is combined with the security transformers' filters before any SQL runs.
    /// </summary>
    public sealed class PivotRequest
    {
        public required IReadOnlyList<string> RowKeys { get; init; }
        public required IReadOnlyList<string> RowKeyGraphQlNames { get; init; }
        public required string PivotColumn { get; init; }
        public required string PivotColumnGraphQlName { get; init; }
        public required string ValueColumn { get; init; }
        public required string Aggregate { get; init; }
        public TableFilter? Filter { get; init; }

        /// <summary>Every column the request references (row keys + pivot + value),
        /// by DB name — asserted against the column-read policy guard so a
        /// policy-denied column cannot be pivoted or aggregated as an oracle.</summary>
        public required IReadOnlyList<string> ReferencedColumns { get; init; }

        public required int MaxPivotColumns { get; init; }
    }

    /// <summary>
    /// Root resolver for a table's PIVOT field
    /// (<c>&lt;table&gt;Pivot(rowKeys, pivotColumn, valueColumn, aggregate, filter)</c>).
    /// Resolves the schema-derived column-enum arguments to real columns, then hands a
    /// <see cref="PivotRequest"/> to <see cref="ISqlExecutionManager.ResolvePivotAsync"/>,
    /// which applies the same fail-closed filter transformers row queries get before it
    /// discovers the pivot values and runs the pivot — so tenant/soft-delete scope
    /// constrains both the column headers and the aggregated cells.
    /// </summary>
    public sealed class PivotTableResolver : IFieldResolver
    {
        private readonly IDbTable _table;

        public PivotTableResolver(IDbTable table) => _table = table;

        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            try
            {
                var bifrost = new BifrostContextAdapter(context);
                var request = BuildRequest(context);
                return await bifrost.Executor.ResolvePivotAsync(new BifrostFieldContextAdapter(context), _table, request);
            }
            catch (BifrostExecutionError ex)
            {
                throw new ExecutionError(ex.Message, ex);
            }
            catch (ArgumentException ex)
            {
                // PivotQueryConfig.Create surfaces shape errors (e.g. the pivot column
                // repeated in rowKeys) as ArgumentException — map to the GraphQL error
                // contract rather than leaking a raw 500.
                throw new ExecutionError(ex.Message, ex);
            }
        }

        private PivotRequest BuildRequest(IResolveFieldContext context)
        {
            var rowKeysArg = context.GetArgument<List<object?>>("rowKeys");
            if (rowKeysArg is not { Count: > 0 })
                throw new BifrostExecutionError($"Pivot of '{_table.GraphQlName}' requires at least one rowKeys column.");
            var rowKeyColumns = rowKeysArg.Select(m => ResolveColumn(m, "rowKeys")).ToList();

            var pivotColumn = ResolveColumn(context.GetArgument<object?>("pivotColumn"), "pivotColumn");
            var valueColumn = ResolveColumn(context.GetArgument<object?>("valueColumn"), "valueColumn");
            var aggregate = context.GetArgument<object?>("aggregate")?.ToString() ?? "count";

            var filterArg = context.GetArgument<Dictionary<string, object?>>("filter");
            var filter = filterArg is { Count: > 0 }
                ? TableFilter.FromObject(filterArg, _table.DbName)
                : null;

            var referenced = rowKeyColumns
                .Append(pivotColumn)
                .Append(valueColumn)
                .Select(c => c.DbName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new PivotRequest
            {
                RowKeys = rowKeyColumns.Select(c => c.DbName).ToList(),
                RowKeyGraphQlNames = rowKeyColumns.Select(c => c.GraphQlName).ToList(),
                PivotColumn = pivotColumn.DbName,
                PivotColumnGraphQlName = pivotColumn.GraphQlName,
                ValueColumn = valueColumn.DbName,
                Aggregate = aggregate,
                Filter = filter,
                ReferencedColumns = referenced,
                MaxPivotColumns = PivotSurface.DefaultMaxPivotColumns,
            };
        }

        /// <summary>
        /// Resolves a column-enum argument member to a model column. Each member is a
        /// schema-derived enum value, so it always maps to a real column; an unmapped
        /// value is a schema/client bug and fails fast rather than reaching SQL.
        /// </summary>
        private ColumnDto ResolveColumn(object? enumMember, string argName)
        {
            var graphQlName = enumMember?.ToString()
                ?? throw new BifrostExecutionError($"Null {argName} column on pivot of '{_table.GraphQlName}'.");
            if (!_table.GraphQlLookup.TryGetValue(graphQlName, out var column))
                throw new BifrostExecutionError($"Unknown {argName} column '{graphQlName}' on pivot of '{_table.GraphQlName}'.");
            return column;
        }
    }
}
