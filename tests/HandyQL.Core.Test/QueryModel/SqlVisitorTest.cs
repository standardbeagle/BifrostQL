using FluentAssertions;
using GraphQLParser;
using GraphQLProxy.Core.QueryModel;
using GraphQLProxy.QueryModel;
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
        public async Task SimpleFieldSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops { id } }");
            await sut.VisitAsync(ast, ctx);
            ctx.Fields.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new
                {
                    Name = "workshops",
                    Alias = "workshops",
                    Fields = new[] { new { Name = "id" } }
                });
        }

        [Fact]
        public async Task ArgumentFieldSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops(sort: [\"id asc\"] limit: 10 ) { data { id }} }");
            await sut.VisitAsync(ast, ctx);
            ctx.Fields.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new
                {
                    Name = "workshops",
                    Alias = "workshops",
                    Fields = new[] { new { Name = "data", Fields = new[] { new { Name = "id"  } } } },
                    Arguments = new object[] { 
                        new { Name = "sort", Value = new string[] { "id asc"} },
                        new { Name = "limit", Value = 10 },
                    }
                });
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public async Task FragmentSuccess(int queryId)
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var graphQL = queryId == 0 ? @"query {
                              workshops {
                                ...wr
                              }
                            }

                            fragment wr on WorkshopsResult {
                              data {
                                id
                                number
                              }
                            }" : @"query {
                              workshops {
                                ...wr
                              }
                            }

                            fragment wr on WorkshopsResult {
                              data {
                                ...uu
                              }
                            }
                            fragment uu on Workshops {
                                id
                                number
                            }";

            var ast = Parser.Parse(graphQL);
            await sut.VisitAsync(ast, ctx);
            ctx.Fields.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new { Name = "workshops", Alias = "workshops" })
                .And.BeOfType<Field>()
                .Which.Fragments.Should().ContainSingle()
                .Which.Should().BeEquivalentTo("wr");

            if (queryId == 0)
            {
                ctx.Fragments.Should().ContainSingle()
                    .Which.Should().BeEquivalentTo(new { Name = "wr" })
                    .And.BeOfType<Field>()
                    .Which.Fields.Should().ContainSingle()
                    .Which.Should().BeEquivalentTo(new { Name = "data", Alias = "data" })
                    .And.BeOfType<Field>()
                    .Which.Fields.Should().HaveCount(2)
                    .And.ContainEquivalentOf(new { Name = "id", Alias = "id" })
                    .And.ContainEquivalentOf(new { Name = "number", Alias = "number" });
            }

            ctx.Fields.Should().ContainSingle()
                .Which.Should().BeOfType<Field>()
                .Which.Fields.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new { Name = "data", Alias = "data" })
                .And.BeOfType<Field>()
                .Which.Fields.Should().HaveCount(2)
                .And.ContainEquivalentOf(new { Name = "id", Alias = "id" })
                .And.ContainEquivalentOf(new { Name = "number", Alias = "number" });
        }
        [Theory]
        [InlineData(0)]
        [InlineData(10)]
        public async Task VariableFieldSuccess(int limit)
        {
            var ctx = new SqlContext()
            {
                Variables = new GraphQL.Validation.Variables()
            };
            ctx.Variables.Add(new GraphQL.Validation.Variable("limit") { Value = limit });
            ctx.Variables.Add(new GraphQL.Validation.Variable("sort") { Value = new string[] { "number desc" } });
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query GetWorkshops($limit: Int, $sort: [String]) { workshops(sort: $sort limit: $limit ) { data { id }} }");
            await sut.VisitAsync(ast, ctx);
            ctx.Fields.Should().Contain(f => f.Name == "workshops");
            var workshops = ctx.Fields.First(f => f.Name == "workshops");
            workshops.Arguments.Should().HaveCount(2);
            workshops.Arguments.Should().Contain(a => a.Name == "sort");
            var sort = workshops.Arguments.First(a => a.Name == "sort");
            ((IEnumerable<object?>)sort.Value!).Should().Contain("number desc");

            workshops.Arguments.Should().Contain(a => a.Name == "limit");
            var limitVar = workshops.Arguments.First(a => a.Name == "limit");
            limitVar.Value.Should().Be(limit);

            workshops.Fields.Should().Contain(f => f.Name == "data");
        }
    }
}
