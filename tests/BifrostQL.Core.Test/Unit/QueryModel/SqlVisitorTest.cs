using FluentAssertions;
using GraphQLParser;
using BifrostQL.Core.QueryModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GraphQLParser.AST;

namespace BifrostQL.Core.QueryModel
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
                    Alias = (string?)null,
                    Fields = new[] { new { Name = "id" } }
                });
        }

        [Fact]
        public async Task SimpleAliasFieldSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { george: workshops { id } }");
            await sut.VisitAsync(ast, ctx);
            ctx.Fields.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new
                {
                    Name = "workshops",
                    Alias = "george",
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
                    Alias = (string?)null,
                    Fields = new[] { new { Name = "data", Fields = new[] { new { Name = "id" } } } },
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
                .Which.Should().BeEquivalentTo(new { Name = "workshops", Alias = (string?)null })
                .And.BeOfType<QueryField>()
                .Which.Fragments.Should().ContainSingle()
                .Which.Should().BeEquivalentTo("wr");

            if (queryId == 0)
            {
                ctx.Fragments.Should().ContainSingle()
                    .Which.Should().BeEquivalentTo(new { Name = "wr" })
                    .And.BeOfType<QueryField>()
                    .Which.Fields.Should().ContainSingle()
                    .Which.Should().BeEquivalentTo(new { Name = "data", Alias = (string?)null })
                    .And.BeOfType<QueryField>()
                    .Which.Fields.Should().HaveCount(2)
                    .And.ContainEquivalentOf(new { Name = "id", Alias = (string?)null })
                    .And.ContainEquivalentOf(new { Name = "number", Alias = (string?)null });
            }

            ctx.Fields.Should().ContainSingle()
                .Which.Should().BeOfType<QueryField>()
                .Which.Fields.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new { Name = "data", Alias = (string?)null })
                .And.BeOfType<QueryField>()
                .Which.Fields.Should().HaveCount(2)
                .And.ContainEquivalentOf(new { Name = "id", Alias = (string?)null })
                .And.ContainEquivalentOf(new { Name = "number", Alias = (string?)null });
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
            var limitDef = new GraphQLVariableDefinition(new GraphQLVariable(new GraphQLName("limit")),
                new GraphQLNamedType(new GraphQLName("number")));
            var sortDef = new GraphQLVariableDefinition(new GraphQLVariable(new GraphQLName("sort")),
                new GraphQLListType(new GraphQLNamedType(new GraphQLName("string"))));
            ctx.Variables.Add(new GraphQL.Validation.Variable("limit", limitDef) { Value = limit });
            ctx.Variables.Add(new GraphQL.Validation.Variable("sort", sortDef) { Value = new string[] { "number desc" } });
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

        [Fact]
        public async Task FloatLiteral_UnderCommaDecimalCulture_ParsesInvariant()
        {
            // A GraphQL float literal is dot-decimal on the wire regardless of host
            // culture. Convert.ToDouble(string) honours CurrentCulture, so under
            // de-DE "1.5" parsed as 15 — a silently wrong filter value. Parse must
            // be invariant-culture.
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            try
            {
                var ctx = new SqlContext();
                var sut = new SqlVisitor();

                var ast = Parser.Parse("query { workshops(filter: { rating: { _eq: 1.5 } }) { id } }");
                await sut.VisitAsync(ast, ctx);

                var filterArg = ctx.Fields.Single().Arguments!.Single(a => a.Name == "filter");
                var filter = (Dictionary<string, object?>)filterArg.Value!;
                var rating = (Dictionary<string, object?>)filter["rating"]!;
                rating["_eq"].Should().Be(1.5d);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public async Task NegativeIntLiteral_UnderNonHyphenNegativeSignCulture_BoxesAsInt()
        {
            // A GraphQL int literal is always ASCII '-' + digits on the wire. int.TryParse(text)
            // / long.TryParse(text) take the CURRENT culture, so under a culture whose
            // NegativeSign is not U+002D both fail and a small negative literal falls through to
            // BigInteger — which the VisitIntValueAsync comment says breaks the `(int?)` casts
            // that read limit/offset arguments. Parse must be Integer + InvariantCulture.
            // (The sign must NOT be U+2212: the runtime always accepts both U+002D and U+2212
            // as minus, so a "−" fixture would be vacuous — verified on net10.0.)
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            var customSign = (System.Globalization.CultureInfo)previous.Clone();
            customSign.NumberFormat.NegativeSign = "NEG"; // not U+002D/U+2212, so '-' fails to parse
            System.Globalization.CultureInfo.CurrentCulture = customSign;
            try
            {
                var ctx = new SqlContext();
                var sut = new SqlVisitor();

                var ast = Parser.Parse("query { workshops(limit: -5) { id } }");
                await sut.VisitAsync(ast, ctx);

                var limitArg = ctx.Fields.Single().Arguments!.Single(a => a.Name == "limit");
                limitArg.Value.Should().BeOfType<int>("small negative literals must box as Int32 for the (int?) casts downstream");
                limitArg.Value.Should().Be(-5);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public async Task AndFilterSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops(filter: { and: [ {startDate: { _gt: \"1-1-2022\"}}, {endDate: { _lt: \"1-1-2023\"}} ]} ) { id } }");
            await sut.VisitAsync(ast, ctx);
            ctx.Fields.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new
                {
                    Name = "workshops",
                    Alias = (string?)null,
                    Fields = new[] { new { Name = "id" } },
                    Arguments = new[] { new { Name = "filter", Value = new [] {
                        new { Key = "and", Value = new object[]
                        {
                            new [] { new { Key = "startDate", Value= new [] { new { Key = "_gt", Value="1-1-2022" } } } },
                            new [] { new { Key = "endDate", Value= new [] { new { Key = "_lt", Value="1-1-2023" } } } },
                        }}}
                    }}
                });
        }
    }
}
