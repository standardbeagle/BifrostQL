using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GraphQL.Types;
using GraphQLParser.AST;

namespace BifrostQL.Core.Schema
{
    internal class DbDirective : Directive
    {
        public DbDirective() : base("Db",DirectiveLocation.FieldDefinition, DirectiveLocation.InputFieldDefinition )
        {
            Description = "Meta data from the database schema such as the dbName in the database, the type, etc.";
            Arguments = new QueryArguments()
            {
                new QueryArgument(new StringGraphType())
                    { Name = "dbName", Description = "The dbName of the type in the database." }
            };
        }

        public override bool? Introspectable { get; } = true;
    }
}
