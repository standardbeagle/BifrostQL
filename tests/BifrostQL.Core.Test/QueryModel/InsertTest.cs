using FluentAssertions;
using GraphQLParser;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel
{
    public sealed class InsertTest
    {
        [Fact]
        public void InsertSimpleObject()
        {
            var tables = SqlVisitorToSqlTest.GetFakeTables();
        }
    }
}
