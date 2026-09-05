using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using GraphQL.Types;
using GraphQL.Utilities;

namespace BifrostQL.Core.Schema
{
    public static class DbSchema
    {
        public static ISchema FromModel(IDbModel model) => FromModel(model, null);

        public static ISchema FromModel(IDbModel model, BifrostProfile? profile)
        {
            // profile reserved for per-profile schema gating (Slice 3)
            var schemaText = SchemaGenerator.SchemaTextFromModel(model);
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