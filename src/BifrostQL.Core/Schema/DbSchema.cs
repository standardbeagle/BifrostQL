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
            => FromModel(model, profile, Array.Empty<Operations.IBifrostOperation>());

        /// <summary>
        /// Builds the endpoint schema with the registered server operations that the model's
        /// <c>operations</c> key exposes. Exposure is resolved once here, so the SDL and the
        /// resolver map are built from the same list.
        /// </summary>
        public static ISchema FromModel(IDbModel model, BifrostProfile? profile,
            IReadOnlyList<Operations.IBifrostOperation> operations)
        {
            // profile reserved for per-profile schema gating (Slice 3)
            var exposed = Operations.OperationExposure.Resolve(model, operations);
            var schemaText = SchemaGenerator.SchemaTextWithOperations(model, exposed);
            var dispatcher = new BifrostDispatcher(model, exposed);
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