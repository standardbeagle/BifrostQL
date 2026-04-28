using FluentAssertions;
using GraphQLParser;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using GraphQL.SystemTextJson;

namespace BifrostQL.Core.QueryModel
{
    public sealed class InsertTest
    {
        [Fact]
        public void CreateSchemaFromTestData()
        {
            var tables = SqlVisitorToSqlTest.GetFakeTables();
            var metadataLoader = new MetadataLoader(Array.Empty<string>());
            var dbModel = DbModel.FromTables(tables, metadataLoader);
            //var schema = DbSchema.FromModel(dbModel);
            //var result = await schema.ExecuteAsync(_ =>
            //{
            //    _.Query = @""
            //})
        }
    }
}
