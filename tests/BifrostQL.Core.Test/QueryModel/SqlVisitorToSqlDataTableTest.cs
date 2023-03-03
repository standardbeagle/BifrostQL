using FluentAssertions;
using GraphQLParser;
using BifrostQL.Core.QueryModel;
using BifrostQL.QueryModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BifrostQL.Core.QueryModel
{
    public sealed class SqlVisitorToSqlDataTableTest
    {
        [Fact]
        public async Task BasicTestSuccess() {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops { data { id number } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalTables();

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new { 
                        TableName = "workshops",
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[] { },
                        Links = new object[] { },
                        ColumnNames = new[] { "id", "number" }
                    });

        }

        [Fact]
        public async Task MultipleTablesTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops { data { id number } } sessions { data { sessionid status } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalTables();

            tables.Should().BeEquivalentTo(
                new[] {
                    new {
                        TableName = "workshops",
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[] { },
                        Links = new object[] { },
                        ColumnNames = new[] { "id", "number" }
                    },
                    new {
                        TableName = "sessions",
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[] { },
                        Links = new object[] { },
                        ColumnNames = new[] { "sessionid", "status" }
                    },
                });
        }

        [Fact]
        public async Task ArgTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops(sort: [\"id desc\", \"number\"], limit: 10, offset: 12 ) { data { id number } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalTables();

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "workshops",
                        Limit = 10,
                        Offset = 12,
                        Sort = new string[] { "id desc", "number" },
                        Joins = new object[] { },
                        Links = new object[] { },
                        ColumnNames = new string[] { "id", "number" }
                    });

        }

        [Fact(Skip = "New Feature")]
        public async Task FilterAndTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops(filter: { and: [ {startDate: { _gt: \"1-1-2022\"}}, {endDate: { _lt: \"1-1-2023\"}} ]} ) { data { id number } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalTables();

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "workshops",
                        Joins = new object[] { },
                        Links = new object[] { },
                        ColumnNames = new string[] { "id", "number" }
                    });

        }

        [Fact]
        public async Task FilterTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops(filter: {id: {_eq: 10} } ) { data { id number } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalTables();

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "workshops",
                        Filter = new { ColumnNames = new[] { "id" }, RelationName = "_eq", Value = 10 },
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[] {},
                        Links = new object[] { },
                        ColumnNames = new string[] { "id", "number" }
                    });
        }

        [Fact]
        public async Task FilterJoinTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { sessions(filter: { workshop: { number: {_eq: \"10-AA\"} } }) { data { id number } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalTables();

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "sessions",
                        Filter = new { ColumnNames = new[] { "workshop", "number" }, RelationName = "_eq", Value = "10-AA" },
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[] { },
                        Links = new object[] { },
                        ColumnNames = new string[] { "id", "number" }
                    });
        }

        [Fact]
        public async Task JoinTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops { data { id _join_test__sessions(on: [\"idd\", \"workshopid\"]) { id } number } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalTables();

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "workshops",
                        Filter = (TableFilter?)null,
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new [] {
                            new {
                                Name = "_join_test__sessions",
                                FromTable = new { TableName = "workshops"},
                                ConnectedTable = new { TableName = "test sessions", ColumnNames = new string[] { "id"}},
                                FromColumn = "idd",
                                ConnectedColumn = "workshopid",
                            }
                        },
                        Links = new object[] { },
                        ColumnNames = new string[] { "id", "number" }
                    });
        }

        [Fact]
        public async Task LinkTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops { data { id sessions { id status } number participants { firstname lastname } } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalTables();

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "workshops",
                        Filter = (TableFilter?)null,
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[] { },
                        Links = new object[] {
                            new {
                                TableName = "sessions",
                                Filter = (TableFilter?)null,
                                Limit = (int?)null,
                                Offset = (int?)null,
                                Sort = new string[] { },
                                Joins = new object[] { },
                                ColumnNames = new string[] { "id", "status" },
                            },
                            new {
                                TableName = "participants",
                                Filter = (TableFilter?)null,
                                Limit = (int?)null,
                                Offset = (int?)null,
                                Sort = new string[] { },
                                Joins = new object[] { },
                                ColumnNames = new string[] { "firstname", "lastname" },
                            },
                        },
                        ColumnNames = new string[] { "id", "number" }
                    });
        }

    }
}
