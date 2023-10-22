using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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

        public string DbDbName { get; init; }
        public string GraphQlDbName { get; init; }
        public AggregateOperationType? AggregateType { get; init; }

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
