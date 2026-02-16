using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using GraphQL.Types;
using GraphQL.Utilities;

namespace BifrostQL.Core.Schema
{
    public static class DbSchema
    {
        public static ISchema FromModel(IDbModel model)
        {
            var includeDynamicJoins = model.GetMetadataBool("dynamic-joins", true);
            var schemaText = SchemaGenerator.SchemaTextFromModel(model, includeDynamicJoins);
            var dispatcher = new BifrostDispatcher(model);
            var schema = GraphQL.Types.Schema.For<DbSchemaBuilder>(schemaText, builder =>
            {
                dispatcher.WireResolvers(builder);
            });
            return schema;
        }
    }

    public enum IdentityType
    {
        None,
        Optional,
        Required
    }
}