using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GraphQL.Utilities;
using GraphQLParser.AST;

namespace BifrostQL.Core.Schema
{
    internal class DbSchemaBuilder : SchemaBuilder
    {
        protected override void PreConfigure(GraphQL.Types.Schema schema)
        {
            schema.Directives.Register(new DbDirective());
            base.PreConfigure(schema);
        }
    }
}
