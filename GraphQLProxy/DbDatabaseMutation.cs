using GraphQL.Types;
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
                    Name = $"insert_{table.TableName}",
                    Arguments = new QueryArguments(
                        new QueryArgument(new DbInputRow(table)) { Name = table.TableName }
                        ),
                    ResolvedType = new IdGraphType() { Name = "id"},
                });
            }
        }
    }
}
