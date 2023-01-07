using FluentAssertions;
using GraphQLParser;
using HandyQL.QueryModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HandyQL.Core.QueryModel
{
    public class SqlVisitorTest
    {
        [Fact]
        public async Task SimpleFieldSuccess() { 
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops { id } }");
            await sut.VisitAsync(ast, ctx);
            ctx.Fields.Should().Contain(f => f.Name == "workshops");
            var workshops = ctx.Fields.First(f => f.Name == "workshops");
            workshops.Fields.Should().Contain(f => f.Name == "id");
        }

        [Fact]
        public async Task ArgumentFieldSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops(order: \"asc\" ) { id } }");
            await sut.VisitAsync(ast, ctx);
            ctx.Fields.Should().Contain(f => f.Name == "workshops");
            var workshops = ctx.Fields.First(f => f.Name == "workshops");
            workshops.Fields.Should().Contain(f => f.Name == "id");
        }
    }
}
