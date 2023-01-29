﻿using GraphQL;
using GraphQL.DataLoader;
using GraphQL.Execution;
using GraphQL.MicrosoftDI;
using GraphQL.Resolvers;
using GraphQL.Types;
using GraphQLParser.AST;
using BifrostQL.Model;
using Microsoft.Extensions.DependencyInjection;
using System.Collections;
using System.Data.Common;
using System.Data.SqlClient;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace BifrostQL.Schema
{
    public class DbDatabaseQuery : ObjectGraphType
    {
        public DbDatabaseQuery(IServiceProvider provider)
        {
            Name = "database";
            var model = provider.GetRequiredService<IDbModel>();
            var tables = model.Tables;

            var rowTypes = tables
                .Select(t => (t.UniqueName, new DbRow(t)))
                .ToDictionary(r => r.UniqueName, r => r.Item2);

            foreach (var row in rowTypes.Values)
            {
                row.AddLinks(rowTypes);
            }

            foreach (var table in tables)
            {
                foreach (var row in rowTypes.Values)
                {
                    row.AddTableJoin(table, rowTypes[table.UniqueName]);
                }
            }

            foreach (var table in tables)
            {
                var filterArgs = new List<QueryArgument>();
                filterArgs.Add(new QueryArgument(new DbColumnFilterType(table)) { Name = "filter" });
                filterArgs.Add(new QueryArgument<IntGraphType>() { Name = "limit" });
                filterArgs.Add(new QueryArgument<IntGraphType>() { Name = "offset" });
                filterArgs.Add(new QueryArgument<ListGraphType<StringGraphType>>() { Name = "sort" });

                var rowType = new DbRow(table);
                AddField(new FieldType
                {
                    Name = table.GraphQlName,
                    Arguments = new QueryArguments(filterArgs),
                    ResolvedType = new TableQueryGraphType(table.GraphQlName, new ListGraphType(rowTypes[table.UniqueName])),
                    Resolver = new DbTableResolver(),
                });
            }
        }
    }

    class TableQueryGraphType : ObjectGraphType
    {
        public TableQueryGraphType(string baseName, GraphType gt)
        {
            Name = $"{baseName}Result";
            Field("data", gt);
            Field<int>("total");
            Field<int>("offset", true);
            Field<int>("limit", true);
        }
    }
}
