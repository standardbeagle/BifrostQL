using FluentAssertions;
using GraphQLParser;
using GraphQLProxy.Core.QueryModel;
using GraphQLProxy.QueryModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace HandyQL.Core.QueryModel
{
    public sealed class SqlVisitorToSqlDataTableTest
    {
        [Fact]
        public async Task BasicTestSuccess() {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops { id number } }");
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

            var ast = Parser.Parse("query { workshops { id number } sessions { sessionid status } }");
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

            var ast = Parser.Parse("query { workshops(sort: [\"id desc\", \"number\"], limit: 10, offset: 12 ) { id number } }");
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

        [Fact]
        public async Task FilterTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops(filter: {id: {_eq: 10} } ) { id number } }");
            await sut.VisitAsync(ast, ctx);
            var tables = ctx.GetFinalTables();

            tables.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(
                    new
                    {
                        TableName = "workshops",
                        Filter = new { ColumnName = "id", RelationName = "_eq", Value = 10 },
                        Limit = (int?)null,
                        Offset = (int?)null,
                        Sort = new string[] { },
                        Joins = new object[] {},
                        Links = new object[] { },
                        ColumnNames = new string[] { "id", "number" }
                    });
        }

        [Fact]
        public async Task JoinTestSuccess()
        {
            var ctx = new SqlContext();
            var sut = new SqlVisitor();

            var ast = Parser.Parse("query { workshops { id _join_test__sessions(on: [\"idd\", \"workshopid\"]) { id } number } }");
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
                        Joins = new object[] {
                            new{
                                Name = "_join_test__sessions",
                                ParentTable = new { TableName = "workshops"},
                                ChildTable = new { TableName = "test sessions", ColumnNames = new string[] { "id"}},
                                ParentColumn = "idd",
                                ChildColumn = "workshopid",
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

            var ast = Parser.Parse("query { workshops { id sessions { id status } number participants { firstname lastname } } }");
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
