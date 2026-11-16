using GraphQL.Utilities;

namespace BifrostQL.Core.Schema
{
    internal class DbSchemaBuilder : SchemaBuilder
    {
        protected override void PreConfigure(GraphQL.Types.Schema schema)
        {
            schema.RegisterType(new JsonScalarGraphType());
            // DateTime that also serializes database STRING representations —
            // SQLite DATETIME columns materialize as strings (see DbDateTimeGraphType).
            schema.RegisterType(new DbDateTimeGraphType());
            // Boolean that also serializes database 0/1 integers — SQLite BOOLEAN/BIT
            // columns materialize as long (see DbBooleanGraphType).
            schema.RegisterType(new DbBooleanGraphType());
            // `_fileUpload(file: Upload!)` is emitted whenever storage metadata exists.
            schema.RegisterType(new UploadGraphType());
            // Bind the SDL's `scalar BigInt` / `scalar Decimal` declarations (emitted
            // by every schema generator) to instances that also accept a decimal
            // string — the only form in which a browser can send a value a JSON
            // number would round. See ExactNumericScalars.
            ExactNumericScalars.Register(schema);
            base.PreConfigure(schema);
        }
    }
}
