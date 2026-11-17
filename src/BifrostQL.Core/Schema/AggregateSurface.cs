using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema
{
    /// <summary>
    /// Single source of truth for the shape of the per-table GROUP BY aggregate
    /// surface (<c>&lt;table&gt;Aggregate</c>). Both schema emission
    /// (<see cref="TableSchemaGenerator"/>) and query execution
    /// (<c>AggregateTableResolver</c>) derive their column sets here, so the SDL and
    /// the SQL can never disagree about which columns are groupable or aggregatable.
    /// </summary>
    public static class AggregateSurface
    {
        /// <summary>GraphQL field selecting the group row count (COUNT(*)).</summary>
        public const string CountField = "_count";

        /// <summary>
        /// The numeric-value aggregate op groups, in emission order. Each maps a
        /// GraphQL field (<c>_sum</c>, …) to its <see cref="AggregateOperationType"/>.
        /// </summary>
        public static readonly IReadOnlyList<(string OpGroup, AggregateOperationType Operation)> ValueOps =
            new (string, AggregateOperationType)[]
            {
                ("_sum", AggregateOperationType.Sum),
                ("_avg", AggregateOperationType.Avg),
                ("_min", AggregateOperationType.Min),
                ("_max", AggregateOperationType.Max),
            };

        /// <summary>Result-set alias for one value column under an op group (e.g. <c>_sum_total</c>).</summary>
        public static string ValueAlias(string opGroup, string columnGraphQlName) => $"{opGroup}_{columnGraphQlName}";

        /// <summary>GraphQL type name for a table's shared aggregate-values object (<c>_sum</c>/<c>_avg</c>/… payload).</summary>
        public static string AggregateFieldsTypeName(IDbTable table) => $"{table.GraphQlName}_aggregateFields";

        /// <summary>GraphQL object type name for one group row.</summary>
        public static string AggregateRowTypeName(IDbTable table) => $"{table.GraphQlName}_aggregate";

        /// <summary>Root query field name for a table's grouped aggregate.</summary>
        public static string AggregateFieldName(IDbTable table) => $"{table.GraphQlName}Aggregate";

        /// <summary>
        /// A column is aggregatable/groupable on the surface unless hidden — the same
        /// visibility rule the row schema applies (<c>visibility: hidden</c>).
        /// </summary>
        public static bool IsVisible(ColumnDto column) =>
            !column.CompareMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden);

        /// <summary>Visible columns, in model order — the groupBy enum members.</summary>
        public static IEnumerable<ColumnDto> GroupableColumns(IDbTable table) =>
            table.Columns.Where(IsVisible);

        /// <summary>
        /// Visible numeric columns — the members of the aggregate-values object and
        /// the operands of SUM/AVG/MIN/MAX. Empty when the table has no numeric
        /// column, in which case the value op groups are not emitted at all.
        /// </summary>
        public static IEnumerable<ColumnDto> NumericColumns(IDbTable table, ITypeMapper typeMapper) =>
            table.Columns.Where(c => IsVisible(c) && IsNumeric(typeMapper.GetGraphQlType(c.EffectiveDataType)));

        /// <summary>
        /// True for the GraphQL scalar types that back SUM/AVG/MIN/MAX. MIN/MAX are
        /// restricted to numeric here to keep the aggregate-values object a numeric
        /// shape (see <see cref="ValueScalar"/>); date/string extrema are a deferred
        /// extension.
        /// </summary>
        public static bool IsNumeric(string graphQlType) => graphQlType switch
        {
            "Int" or "Short" or "Byte" or "BigInt" or "Decimal" or "Float" => true,
            _ => false,
        };

        /// <summary>
        /// The GraphQL scalar of one column's field on the aggregate-values object,
        /// shared by every op group. A <c>Decimal</c> or <c>BigInt</c> column reads as
        /// <c>Decimal</c>: SUM/AVG/MIN/MAX of an exact column are exact in every
        /// supported dialect, and a <c>Float</c> field would round them to a double
        /// before the client saw them (a bigint SUM past 2^53, a NUMERIC(38,10) value
        /// past 15–17 digits). Every other numeric column reads as <c>Float</c> (AVG of
        /// an integer column is fractional).
        /// </summary>
        public static string ValueScalar(string columnGraphQlType) =>
            columnGraphQlType is "Decimal" or "BigInt" ? "Decimal" : "Float";

        /// <summary>
        /// The wire form of one per-row <c>_agg</c> result, by the same rule as the
        /// grouped surface: COUNT is <c>Int</c> (the grouped <c>_count</c>), and
        /// SUM/AVG/MIN/MAX take <see cref="ValueScalar"/> of the aggregated column.
        /// <c>_agg</c> names its column at query time, so its one SDL type is the
        /// <see cref="AggregateNumberGraphType"/> scalar, which writes each of these
        /// forms; this decides which one a field's values take.
        /// </summary>
        public static string RowAggregateScalar(AggregateOperationType operation, string columnGraphQlType) =>
            operation == AggregateOperationType.Count ? "Int" : ValueScalar(columnGraphQlType);
    }
}
