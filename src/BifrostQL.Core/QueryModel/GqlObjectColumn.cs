using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Schema;

namespace BifrostQL.Core.QueryModel
{
    public sealed class GqlObjectColumn
    {
        public GqlObjectColumn(string dbName)
        {
            DbDbName = dbName;
            GraphQlDbName = dbName;
            AggregateType = AggregateOperationType.None;
        }

        public GqlObjectColumn(string dbName, string graphQlName)
        {
            DbDbName = dbName;
            GraphQlDbName = graphQlName;
            AggregateType = AggregateOperationType.None;
        }

        public GqlObjectColumn(string dbName, string graphQlName, AggregateOperationType aggregateType)
        {
            DbDbName = dbName;
            GraphQlDbName = graphQlName;
            AggregateType = aggregateType;
        }

        public GqlObjectColumn(ComputedColumnDefinition computedColumn, string graphQlName)
        {
            DbDbName = computedColumn.Name;
            GraphQlDbName = graphQlName;
            AggregateType = AggregateOperationType.None;
            ComputedColumn = computedColumn;
        }

        public string DbDbName { get; init; }
        public string GraphQlDbName { get; init; }
        public AggregateOperationType? AggregateType { get; init; }
        public ComputedColumnDefinition? ComputedColumn { get; init; }
        public bool IsProviderComputed => ComputedColumn?.Kind == ComputedColumnKind.Provider;
        public bool IsSqlComputed => ComputedColumn?.Kind == ComputedColumnKind.Sql;

        public string ToSelectSql(IDbModel model, IDbTable table, ISqlDialect dialect, string? tableAlias = null, bool useAsKeyword = false)
        {
            var aliasSeparator = useAsKeyword ? " AS " : " ";
            if (ComputedColumn is { Kind: ComputedColumnKind.Sql } sqlComputed)
                return $"{sqlComputed.RenderSqlExpression(table, dialect, tableAlias)}{aliasSeparator}{dialect.EscapeIdentifier(GraphQlDbName)}";

            if (ComputedColumn is { Kind: ComputedColumnKind.Provider })
                throw new InvalidOperationException("Provider computed columns are populated after SQL execution.");

            var expr = string.IsNullOrWhiteSpace(tableAlias)
                ? dialect.EscapeIdentifier(DbDbName)
                : $"{dialect.EscapeIdentifier(tableAlias)}.{dialect.EscapeIdentifier(DbDbName)}";

            if (table.ColumnLookup.TryGetValue(DbDbName, out var col)
                && dialect.RequiresTextCast(col.DataType, model.TypeMapper.GetGraphQlType(col.EffectiveDataType)))
                expr = dialect.TextCast(expr, col.DataType);

            return $"{expr}{aliasSeparator}{dialect.EscapeIdentifier(GraphQlDbName)}";
        }

        public string GetSqlColumn()
        {
            return AggregateType switch
            {
                AggregateOperationType.None => $"[{DbDbName}] [{GraphQlDbName}]",
                _ => $"{AggregateType}([{DbDbName}]) [{GraphQlDbName}]"
            };
        }
    }
}
