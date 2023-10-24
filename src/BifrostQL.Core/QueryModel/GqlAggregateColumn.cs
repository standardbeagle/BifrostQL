using BifrostQL.Core.Schema;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.QueryModel
{
    public sealed class GqlAggregateColumn
    {
        public GqlAggregateColumn(string name)
        {
            Name = name;
            GraphQlName = name;
            AggregateType = AggregateOperationType.None;
        }

        public GqlAggregateColumn(string name, string graphQlName)
        {
            Name = name;
            GraphQlName = graphQlName;
            AggregateType = AggregateOperationType.None;
        }

        public GqlAggregateColumn(string name, string graphQlName, AggregateOperationType aggregateType)
        {
            Name = name;
            GraphQlName = graphQlName;
            AggregateType = aggregateType;
        }

        public string Name { get; init; }
        public string GraphQlName { get; init; }
        public AggregateOperationType? AggregateType { get; init; }

        public string GetSqlColumn()
        {
            return AggregateType switch
            {
                AggregateOperationType.None => $"[{Name}] [{GraphQlName}]",
                _ => $"{AggregateType}([{Name}]) [{GraphQlName}]"
            };
        }
    }
}
