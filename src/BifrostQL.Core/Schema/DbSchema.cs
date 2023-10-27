using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;

namespace BifrostQL.Core.Schema
{
    public static class DbSchema
    {

        public static ISchema SchemaFromModel(IDbModel model, bool includeDynamicJoins)
        {
            var schemaText = SchemaGenerator.SchemaTextFromModel(model, includeDynamicJoins);
            var schema = GraphQL.Types.Schema.For<DbSchemaBuilder>(schemaText, _ =>
            {
                var query = _.Types.For("database");
                var mut = _.Types.For("databaseInput");
                foreach (var table in model.Tables)
                {
                    var tableField = query.FieldFor(table.GraphQlName);
                    tableField.Resolver = new DbTableResolver(table);

                    var tableInsertField = mut.FieldFor(table.GraphQlName);
                    tableInsertField.Resolver = new DbTableMutateResolver(table);

                    var tableType = _.Types.For(table.GraphQlName);
                    var aggregateType = tableType.FieldFor("_agg");
                    aggregateType.Resolver = DbJoinFieldResolver.Instance;

                    foreach (var column in table.Columns)
                    {
                        var columnField = tableType.FieldFor(column.GraphQlName);
                        columnField.Resolver = DbJoinFieldResolver.Instance;
                    };
                    foreach (var singleLink in table.SingleLinks)
                    {
                        var columnField = tableType.FieldFor(singleLink.Value.ParentTable.GraphQlName);
                        columnField.Resolver = DbJoinFieldResolver.Instance;
                    };
                    foreach (var multiLink in table.MultiLinks)
                    {
                        var columnField = tableType.FieldFor(multiLink.Value.ChildTable.GraphQlName);
                        columnField.Resolver = DbJoinFieldResolver.Instance;
                    };
                    foreach (var joinTable in model.Tables)
                    {
                        var joinField = tableType.FieldFor(joinTable.JoinFieldName);
                        joinField.Resolver = DbJoinFieldResolver.Instance;
                        var singleField = tableType.FieldFor(joinTable.SingleFieldName);
                        singleField.Resolver = DbJoinFieldResolver.Instance;
                    }
                }

                var dbSchema = query.FieldFor("_dbSchema");
                dbSchema.Resolver = new DbSchemaResolver(model);

            });
            return schema;
        }
    }

    //internal record NameMatcher {
    //    public string PrimaryGqlTableName { get; set; }
    //    public string NestedGqlTableName { get; set; }
    //}

    public enum IdentityType
    {
        None,
        Optional,
        Required
    }
}