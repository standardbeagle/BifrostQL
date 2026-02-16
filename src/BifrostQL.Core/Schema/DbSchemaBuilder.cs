using GraphQL.Utilities;

namespace BifrostQL.Core.Schema
{
    internal class DbSchemaBuilder : SchemaBuilder
    {
        protected override void PreConfigure(GraphQL.Types.Schema schema)
        {
            schema.RegisterType(new JsonScalarGraphType());
            base.PreConfigure(schema);
        }
    }
}
