using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema
{
    /// <summary>
    /// Single source of truth for the shape of the per-table PIVOT surface
    /// (<c>&lt;table&gt;Pivot</c>). Both schema emission (<see cref="TableSchemaGenerator"/>
    /// / <see cref="SchemaGenerator"/>) and query execution (<c>PivotTableResolver</c> /
    /// <c>SqlExecutionManager.ResolvePivotAsync</c>) derive their names here, so the SDL
    /// and the SQL can never disagree about the field name, the aggregate-function enum,
    /// or the cardinality cap.
    /// </summary>
    public static class PivotSurface
    {
        /// <summary>Global GraphQL enum naming the pivot aggregate functions.</summary>
        public const string AggregateEnumName = "PivotAggregate";

        /// <summary>
        /// Default cap on the number of distinct pivot-column values. A pivot expands
        /// one output column per distinct value, so an unbounded pivot column would
        /// generate a runaway-wide result set and SQL statement. Above the cap the
        /// resolver errors with steering rather than truncating — a truncated pivot
        /// would silently drop columns and misrepresent the data.
        /// </summary>
        public const int DefaultMaxPivotColumns = 100;

        /// <summary>Root query field name for a table's pivot.</summary>
        public static string PivotFieldName(IDbTable table) => $"{table.GraphQlName}Pivot";

        /// <summary>
        /// SDL for the global aggregate-function enum, emitted once for the whole
        /// schema. Lowercase members match the operator-casing convention used
        /// elsewhere in the generated schema and parse case-insensitively into
        /// <see cref="QueryModel.PivotAggregateFunction"/>.
        /// </summary>
        public static string AggregateEnumDefinition() =>
            $"enum {AggregateEnumName} {{ count sum avg min max }}";
    }
}
