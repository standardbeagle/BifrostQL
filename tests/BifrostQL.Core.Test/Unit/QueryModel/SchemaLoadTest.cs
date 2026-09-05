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
        [Fact]
        public void FakeSchemaLoads()
        {
            var model = new DbModel { Tables = SqlVisitorToSqlTest.GetFakeTables(), Metadata = new Dictionary<string, object?>() };
            var schema = DbSchema.FromModel(model);
        }
    }
}
