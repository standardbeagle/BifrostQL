using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;

namespace BifrostQL.Core.QueryModel
{
    public sealed class SchemaLoadTest
    {
        [Fact(Skip = "This will take some work")]
        public void FakeSchemaLoadsWithDynamicJoins()
        {
            var model = new DbModel { Tables = SqlVisitorToSqlTest.GetFakeTables() };
            var schema = DbSchema.SchemaFromModel(model, true);
        }

        [Fact(Skip = "This will take some work")]
        public void FakeSchemaLoadsWithoutDynamicJoins()
        {
            var model = new DbModel { Tables = SqlVisitorToSqlTest.GetFakeTables() };
            var schema = DbSchema.SchemaFromModel(model, false);
        }
    }
}
