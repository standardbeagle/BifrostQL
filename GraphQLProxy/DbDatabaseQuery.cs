using GraphQL;
using GraphQL.DataLoader;
using GraphQL.Execution;
using GraphQL.MicrosoftDI;
using GraphQL.Resolvers;
using GraphQL.Types;
using GraphQLParser.AST;
using GraphQLProxy.Model;
using System.Collections;
using System.Data.Common;
using System.Data.SqlClient;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using static GraphQLProxy.ReaderEnum;

namespace GraphQLProxy
{
    public class DbDatabaseQuery : ObjectGraphType
    {
        private readonly IDbConnFactory _dbConnFactory;
        public DbDatabaseQuery(IDbModel model, IDbConnFactory connFactory, IDataLoaderContextAccessor dlContext)
        {
            Name = "database";
            _dbConnFactory = connFactory;
            var tables = model.Tables;

            var context = dlContext.Context;

            var rowTypes = tables
                .Select(t => (t.TableName, new DbRow(t)))
                .ToDictionary(r => r.TableName, r => r.Item2);

            foreach (var table in tables)
            {
                foreach(var row in rowTypes.Values)
                {
                    row.AddTableJoin(table, rowTypes[table.TableName]);
                }
            }

            foreach (var table in tables)
            {
                var filterArgs = new List<QueryArgument>();
                filterArgs.Add(new QueryArgument(new DbColumnFilterType(table.GraphQLName, table.Columns)) { Name = "filter" });
                filterArgs.Add(new QueryArgument<IntGraphType>() { Name = "limit" });
                filterArgs.Add(new QueryArgument<IntGraphType>() { Name = "offset" });
                filterArgs.Add(new QueryArgument<ListGraphType<StringGraphType>>() { Name = "sort" });

                var rowType = new DbRow(table);
                AddField(new FieldType
                {
                    Name = table.GraphQLName,
                    Arguments = new QueryArguments(filterArgs),
                    ResolvedType = new ListGraphType(rowTypes[table.TableName]),
                    Resolver = new DbTableResolver(_dbConnFactory),
                });
            }
        }
    }
}
