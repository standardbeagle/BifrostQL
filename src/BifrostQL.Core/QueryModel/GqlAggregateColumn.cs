using BifrostQL.Core.Schema;
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
            var (firstDirection, firstLink) = Links[0];
            var sql = $"SELECT {firstLink.GetSqlSourceColumns(firstDirection, columnName: "joinId")}, {firstLink.GetSqlSourceColumns(firstDirection, columnName: "srcId")} FROM {firstLink.GetSqlSourceTableRef(firstDirection)}{filterSql.Sql}";

            for (var i = 0; i < Links.Count; ++i)
            {
                var (direction, link) = Links[i];
                var fromSql = $" FROM ({sql}) [src] INNER JOIN {link.GetSqlDestTableRef(direction)} [next] ON [src].[joinId] = [next].[{link.GetSqlDestJoinColumn(direction)}]";
                var selectSql = (i, Links.Count - i) switch
                {
                    (_, 1) => $"SELECT [src].[srcId], {AggregateType}([next].{dialect.EscapeIdentifier(FinalColumnName)}) {dialect.EscapeIdentifier(FinalColumnGraphQlName)}",
                    _ => $"SELECT {link.GetSqlSourceColumns(direction, columnName: "joinId", tableName: "next")}, [src].[srcId]",
                };
                sql = selectSql + fromSql;
            }

            return new ParameterizedSql(sql + " GROUP BY [src].[srcId]", filterSql.Parameters);
        }
    }
    public enum LinkDirection
    {
        OneToMany,
        ManyToOne,
    }


}
