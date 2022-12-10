using GraphQL.Types;
using GraphQLParser.AST;
using GraphQLProxy.Model;
using System.Xml.Linq;

namespace GraphQLProxy
{
    public sealed class DbDatabaseMutation : ObjectGraphType
    {
        private readonly IDbConnFactory _dbConnFactory;
        public DbDatabaseMutation(IDbModel model, IDbConnFactory connFactory)
        {
            Name = "mutation";
            _dbConnFactory = connFactory;

            foreach (var table in model.Tables)
            {
                AddField(new FieldType
                {
                    Name = table.FullName,
                    Arguments = new QueryArguments(
                        new QueryArgument(new DbInputRow("insert", table, IdentityType.None)) { Name = "insert" },
                        new QueryArgument(new DbInputRow("update", table, IdentityType.Required)) { Name = "update" },
                        new QueryArgument(new DbInputRow("upsert", table, IdentityType.Optional)) { Name = "upsert" },
                        new QueryArgument<IdGraphType>() { Name = "delete" }
                        ),
                    ResolvedType = new IdGraphType() { Name = "id"},
                    Resolver = new DbTableMutateResolver(),
                });
            }
        }
    }
}
