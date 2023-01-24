﻿using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using GraphQL;
using GraphQL.Conversion;
using GraphQL.Instrumentation;
using GraphQL.Introspection;
using GraphQL.Types;
using GraphQL.Utilities;
using GraphQLParser;
using BifrostQL.Model;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Schema
{
    public class DbSchema : GraphQL.Types.Schema
    {
        public DbSchema(IServiceProvider provider)
            : base(provider)
        {
            Query = provider.GetRequiredService<DbDatabaseQuery>();
            Mutation = provider.GetRequiredService<DbDatabaseMutation>();
        }
    }
}