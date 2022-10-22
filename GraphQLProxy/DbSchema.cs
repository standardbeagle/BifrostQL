using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using GraphQL;
using GraphQL.Conversion;
using GraphQL.Instrumentation;
using GraphQL.Introspection;
using GraphQL.Types;
using GraphQL.Utilities;
using GraphQLParser;
using GraphQLProxy.Model;

namespace GraphQLProxy
{
    public class DbSchema : Schema
    {
        //private readonly DbModel _model;
        public DbSchema(IDbModel model, IDbConnFactory conFactory)
        {
            Query = new DbDatabase(model.Tables, conFactory);
        }
    }
}
