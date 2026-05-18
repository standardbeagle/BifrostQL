using BifrostQL.Core.Schema;
using BifrostQL.Core.Resolvers;
using GraphQLParser.AST;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using GraphQL.Types;

namespace BifrostQL.Core.QueryModel
{
    public sealed class GqlAggregateColumn
    {
        public GqlAggregateColumn(List<(LinkDirection direction, TableLinkDto link)> links, string name, string graphQlName, AggregateOperationType aggregateType)
        {
            Links = links;
            FinalColumnName = name;
            FinalColumnGraphQlName = graphQlName;
            AggregateType = aggregateType;
        }

        public List<(LinkDirection direction, TableLinkDto link)> Links { get; init; }
        public string FinalColumnName { get; init; }
        public string FinalColumnGraphQlName { get; init; }
        public string? SqlKey { get; set; }

        public AggregateOperationType? AggregateType { get; init; }

        public ParameterizedSql ToSqlParameterized(ISqlDialect dialect, ParameterizedSql filterSql)
        {
            if (Links.Count == 0)
                throw new BifrostExecutionError(
                    "Aggregate value must include at least one nested-FK link (e.g. " +
                    "`value: { joinTable: { column: foo } }`); bare-column aggregates " +
                    "without a join are not supported on `_agg` inside the per-row " +
                    "`data` selection.");

            var src = dialect.EscapeIdentifier("src");
            var next = dialect.EscapeIdentifier("next");
            var joinId = dialect.EscapeIdentifier("joinId");
            var srcId = dialect.EscapeIdentifier("srcId");

            var (firstDirection, firstLink) = Links[0];
            var sql = $"SELECT {firstLink.GetSqlSourceColumns(dialect, firstDirection, columnName: "joinId")}, {firstLink.GetSqlSourceColumns(dialect, firstDirection, columnName: "srcId")} FROM {firstLink.GetSqlSourceTableRef(dialect, firstDirection)}{filterSql.Sql}";

            for (var i = 0; i < Links.Count; ++i)
            {
                var (direction, link) = Links[i];
                var fromSql = $" FROM ({sql}) {src} INNER JOIN {link.GetSqlDestTableRef(dialect, direction)} {next} ON {src}.{joinId} = {next}.{dialect.EscapeIdentifier(link.GetSqlDestJoinColumn(direction))}";
                var selectSql = (i, Links.Count - i) switch
                {
                    (_, 1) => $"SELECT {src}.{srcId}, {AggregateType}({next}.{dialect.EscapeIdentifier(FinalColumnName)}) {dialect.EscapeIdentifier(FinalColumnGraphQlName)}",
                    _ => $"SELECT {link.GetSqlSourceColumns(dialect, direction, columnName: "joinId", tableName: "next")}, {src}.{srcId}",
                };
                sql = selectSql + fromSql;
            }

            return new ParameterizedSql(sql + $" GROUP BY {src}.{srcId}", filterSql.Parameters);
        }
    }
    public enum LinkDirection
    {
        OneToMany,
        ManyToOne,
    }


}
