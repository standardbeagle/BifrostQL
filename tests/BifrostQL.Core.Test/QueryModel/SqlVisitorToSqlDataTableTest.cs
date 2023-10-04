using FluentAssertions;
using GraphQLParser;
using BifrostQL.Core.QueryModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel
{
    public sealed class SqlVisitorToSqlDataTableTest
    {
        [Fact]
        public async Task BasicTestSuccess() {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { work__shops { data { id number } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalQueries(GetFakeModel());

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new { 
                        TableName = "work shops",
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[] { },
                        Links = new object[] { },
                        //ColumnNames = new[] { "id", "number" }
                    });

        }

        private static IDbModel GetFakeModel()
        {
            return new DbModel { Tables = SqlVisitorToSqlTest.GetFakeTables() };
        }

        [Fact]
        public async Task MultipleTablesTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { work__shops { data { id number } } sessions { data { id status } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalQueries(GetFakeModel());

            tables.Should().BeEquivalentTo(
                new[] {
                    new {
                        TableName = "work shops",
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[] { },
                        Links = new object[] { },
                        //ColumnNames = new[] { "id", "number" }
                    },
                    new {
                        TableName = "sessions",
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[] { },
                        Links = new object[] { },
                        //ColumnNames = new[] { "sessionid", "status" }
                    },
                });
        }

        [Fact]
        public async Task ArgTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { work__shops(sort: [id_desc, number_asc], limit: 10, offset: 12 ) { data { id number } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalQueries(GetFakeModel());

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "work shops",
                        Limit = 10,
                        Offset = 12,
                        Sort = new [] { "id_desc", "number_asc" },
                        Joins = new object[] { },
                        Links = new object[] { },
                        //ColumnNames = new [] { "id", "number" }
                    });

        }

        [Fact]
        public async Task FilterAndTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { work__shops(filter: { and: [ {startDate: { _gt: \"1-1-2022\"}}, {endDate: { _lt: \"1-1-2023\"}} ]} ) { data { id number } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalQueries(GetFakeModel());

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "work shops",
                        Filter = new { And = new object[] { new { ColumnName = "startDate", Next = new { RelationName = "_gt", Value = "1-1-2022" } }, new { ColumnName = "endDate", Next = new { RelationName = "_lt", Value = "1-1-2023" } } } },
                        Joins = new object[] { },
                        Links = new object[] { },
                        //ColumnNames = new string[] { "id", "number" }
                    });

        }

        [Fact]
        public async Task FilterOrTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { work__shops(filter: { and: [ {startDate: { _gt: \"1-1-2022\"}}, {endDate: { _lt: \"1-1-2023\"}} ]} ) { data { id number } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalQueries(GetFakeModel());

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "work shops",
                        Filter = new { And = new object[] { new { ColumnName = "startDate", Next = new { RelationName = "_gt", Value = "1-1-2022" } }, new { ColumnName = "endDate", Next = new { RelationName = "_lt", Value = "1-1-2023" } } } },
                        Joins = new object[] { },
                        Links = new object[] { },
                        //ColumnNames = new string[] { "id", "number" }
                    });

        }

        [Fact]
        public async Task FilterTestNullSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { work__shops(filter: null ) { data { id number } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalQueries(GetFakeModel());

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "work shops",
                        Filter = (TableFilter?)null,
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[] { },
                        Links = new object[] { },
                        //ColumnNames = new string[] { "id", "number" }
                    });
        }

        [Fact]
        public async Task FilterTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { work__shops(filter: {id: {_eq: 10} } ) { data { id number } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalQueries(GetFakeModel());

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "work shops",
                        Filter = new { ColumnName =  "id", Next = new { RelationName = "_eq", Value = 10 }},
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[] {},
                        Links = new object[] { },
                        //ColumnNames = new string[] { "id", "number" }
                    });
        }

        [Fact]
        public async Task FilterJoinTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { sessions(filter: { workshop: { number: {_eq: \"10-AA\"} } }) { data { id status } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalQueries(GetFakeModel());

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "sessions",
                        Filter = new { ColumnName = "workshop", Next = new { ColumnName = "number", Next = new { RelationName = "_eq", Value = "10-AA" } }},
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[] { },
                        Links = new object[] { },
                        //ColumnNames = new string[] { "id", "number" }
                    });
        }

        [Fact]
        public async Task JoinTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { work__shops { data { id _join_sessions(on: {idd: {_eq: workshopid } }) { id } number } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalQueries(GetFakeModel());

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "work shops",
                        Filter = (TableFilter?)null,
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Path = "work__shops",
                        Joins = new [] {
                            new {
                                Name = "_join_sessions",
                                FromTable = new { TableName = "work shops"},
                                ConnectedTable = new
                                {
                                    TableName = "sessions", 
                                    //ColumnNames = new string[] { "id"}
                                },
                                FromColumn = "idd",
                                ConnectedColumn = "workshopid",
                                Path = "work__shops/_join_sessions",
                            }
                        },
                        Links = new object[] { },
                        //ColumnNames = new string[] { "id", "number" }
                    });
        }

        [Fact]
        public async Task LinkTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { work__shops { data { id sessions { id status } number participants__table { firstname lastname } } } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalQueries(GetFakeModel());

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "work shops",
                        Filter = (TableFilter?)null,
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[]
                        {
                            new
                            {
                                Name = "sessions",
                            },
                            new
                            {
                                Name = "participants__table",
                            }
                        },
                        Links = new object[] {
                            new {
                                TableName = "sessions",
                                Filter = (TableFilter?)null,
                                Limit = (int?)null,
                                Offset = (int?)null,
                                Sort = new string[] { },
                                Joins = new object[] { },
                                //ColumnNames = new string[] { "id", "status" },
                            },
                            new {
                                TableName = "participants table",
                                Filter = (TableFilter?)null,
                                Limit = (int?)null,
                                Offset = (int?)null,
                                Sort = new string[] { },
                                Joins = new object[] { },
                                //ColumnNames = new string[] { "firstname", "lastname" },
                            },
                        },
                        //ColumnNames = new string[] { "id", "number" }
                    });
        }

    }
}
