using FluentAssertions;
using GraphQLParser;
using BifrostQL.Core.QueryModel;
using BifrostQL.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel
{
    public sealed class SqlVisitorToSqlTest
    {
        [Fact]
        public async Task SimpleQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { work__shops { data { id } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables(model).Select(t => t.ToSql(model)).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work shops_count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                });

        }

        [Fact]
        public async Task SimpleQueryNonStandardColumnSuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { work__shops { data { id percentage_34 } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables(model).Select(t => t.ToSql(model)).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work shops", "SELECT [id] [id],[percentage%] [percentage_34] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work shops_count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                });

        }

        [Fact]
        public async Task SimpleJoinQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { work__shops { data { id sess:_join_sessions(on: {id: {_neq: workshopid}}) { id status } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables(model).Select(t => t.ToSql(model)).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work shops_count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work shops->sess", "SELECT [a].[JoinId] [src_id], [b].[sid] AS [id],[b].[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [work shops]) [a] INNER JOIN [sessions] [b] ON [a].[JoinId] != [b].[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                });

        }

        [Fact]
        public async Task SimpleJoinNonStandardColumnQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { work__shops { data { id sess:_join_sessions(on: {id: {_neq: workshopid}}) { id status } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables(model).Select(t => t.ToSql(model)).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work shops_count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work shops->sess", "SELECT [a].[JoinId] [src_id], [b].[sid] AS [id],[b].[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [work shops]) [a] INNER JOIN [sessions] [b] ON [a].[JoinId] != [b].[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                });

        }

        [Fact]
        public async Task SimpleLinkQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { work__shops { data { id sess:sessions { id status } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables(model).Select(t => t.ToSql(model)).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work shops_count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work shops->sess", "SELECT [a].[JoinId] [src_id], [b].[sid] AS [id],[b].[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [work shops]) [a] INNER JOIN [sessions] [b] ON [a].[JoinId] = [b].[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                });

        }
        [Fact]
        public async Task SimpleLinkQueryFilterSuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { work__shops { data { id sess:sessions(filter: { status: {_eq : 0} }) { id status } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables(model).Select(t => t.ToSql(model)).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work shops_count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work shops->sess", "SELECT [a].[JoinId] [src_id], [b].[sid] AS [id],[b].[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [work shops]) [a] INNER JOIN [sessions] [b] ON [a].[JoinId] = [b].[workshopid] WHERE [b].[status] = '0' ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                });

        }

        [Fact]
        public async Task MultipleFilterSameTableQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse(
                "query { work__shops { data { id } } work__shops(filter: null) { data { id } } work__shops(filter: { id: { _eq: 1} }) { data { id } }}");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables(model).Select(t => t.ToSql(model)).ToArray();
            sqls.Should().HaveCount(3);
            sqls[0].Should().Equal(new Dictionary<string, string>
            {
                { "work shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                { "work shops_count", "SELECT COUNT(*) FROM [dbo].[work shops]" },
            });
            sqls[1].Should().Equal(new Dictionary<string, string>
            {
                { "work shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                { "work shops_count", "SELECT COUNT(*) FROM [dbo].[work shops]" },
            });
            sqls[2].Should().Equal(new Dictionary<string, string>
            {
                { "work shops", "SELECT [id] [id] FROM [dbo].[work shops] WHERE [work shops].[id] = '1' ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                { "work shops_count", "SELECT COUNT(*) FROM [dbo].[work shops] WHERE [work shops].[id] = '1'" },
            });
        }

        [Fact]
        public async Task SimpleSingleLinkQueryException()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { sessions { data { id work__shops { id number } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables(model).Select(t => t.ToSql(model)).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "sessions", "SELECT [sid] [id],[workshopid] [workshopid] FROM [dbo].[sessions] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "sessions_count", "SELECT COUNT(*) FROM [dbo].[sessions]"},
                    { "sessions->work__shops", "SELECT [a].[JoinId] [src_id], [b].[id] AS [id],[b].[number] AS [number] FROM (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions]) [a] INNER JOIN [work shops] [b] ON [a].[JoinId] = [b].[id]" },
                });
        }

        [Fact]
        public async Task SimpleSingleLinkQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { sessions { data { id work__shops { id number } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables(model).Select(t => t.ToSql(model)).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "sessions", "SELECT [sid] [id],[workshopid] [workshopid] FROM [dbo].[sessions] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "sessions_count", "SELECT COUNT(*) FROM [dbo].[sessions]"},
                    { "sessions->work__shops", "SELECT [a].[JoinId] [src_id], [b].[id] AS [id],[b].[number] AS [number] FROM (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions]) [a] INNER JOIN [work shops] [b] ON [a].[JoinId] = [b].[id]" },
                });
        }

        [Fact]
        public async Task SimpleDynamicSingleLinkQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { sessions { data { id workshop: _single_Work__shops(on: {workshopid: {_eq: id }}) { id number } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables(model).Select(t => t.ToSql(model)).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "sessions", "SELECT [sid] [id],[workshopid] [workshopid] FROM [dbo].[sessions] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "sessions_count", "SELECT COUNT(*) FROM [dbo].[sessions]"},
                    { "sessions->workshop", "SELECT [a].[JoinId] [src_id], [b].[id] AS [id],[b].[number] AS [number] FROM (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions]) [a] INNER JOIN [work shops] [b] ON [a].[JoinId] = [b].[id]" },
                });

        }

        public static List<TableDto> GetFakeTables()
        {
            var workshops = new TableDto
            {
                TableSchema = "dbo",
                DbName = "work shops",
                GraphQlName = "work__shops",
                ColumnLookup = new Dictionary<string, ColumnDto> {
                        { "id", new ColumnDto { TableName = "work shops", ColumnName= "id", IsPrimaryKey= true } },
                        { "number", new ColumnDto { TableName = "work shops", ColumnName= "number", IsPrimaryKey= false } },
                        { "percentage%", new ColumnDto { TableName = "work shops", ColumnName= "percentage%", IsPrimaryKey= false } },
                },
                GraphQlLookup = new Dictionary<string, ColumnDto> {
                    { "id", new ColumnDto { TableName = "work shops", ColumnName= "id", IsPrimaryKey= true } },
                    { "number", new ColumnDto { TableName = "work shops", ColumnName= "number", IsPrimaryKey= false } },
                    { "percentage_34", new ColumnDto { TableName = "work shops", ColumnName= "percentage%", IsPrimaryKey= false } },
                },
            };
            var sessions = new TableDto
            {
                TableSchema = "dbo",
                DbName = "sessions",
                GraphQlName = "sessions",
                ColumnLookup = new Dictionary<string, ColumnDto> {
                        { "id", new ColumnDto { TableName = "sessions", ColumnName= "sid", IsPrimaryKey= true } },
                        { "status", new ColumnDto { TableName = "sessions", ColumnName= "status", IsPrimaryKey= false } },
                        { "workshopid", new ColumnDto { TableName = "sessions", ColumnName= "workshopid", IsPrimaryKey= false } },
                        { "percentage%", new ColumnDto { TableName = "sessions", ColumnName= "percentage%", IsPrimaryKey= false } },
                    },
                GraphQlLookup = new Dictionary<string, ColumnDto> {
                    { "id", new ColumnDto { TableName = "sessions", ColumnName= "sid", IsPrimaryKey= true } },
                    { "status", new ColumnDto { TableName = "sessions", ColumnName= "status", IsPrimaryKey= false } },
                    { "workshopid", new ColumnDto { TableName = "sessions", ColumnName= "workshopid", IsPrimaryKey= false } },
                    { "percentage_34", new ColumnDto { TableName = "sessions", ColumnName= "percentage%", IsPrimaryKey= false } },
                },
            };
            var participants = new TableDto
            {
                TableSchema = "dbo",
                DbName = "participants table",
                GraphQlName = "participants__table",
                ColumnLookup = new Dictionary<string, ColumnDto> {
                    { "id", new ColumnDto { TableName = "sessions", ColumnName= "sid", IsPrimaryKey= true } },
                    { "status code", new ColumnDto { TableName = "sessions", ColumnName= "status code", IsPrimaryKey= false } },
                    { "firstname", new ColumnDto { TableName = "sessions", ColumnName= "firstname", IsPrimaryKey= false } },
                    { "lastname", new ColumnDto { TableName = "sessions", ColumnName= "lastname", IsPrimaryKey= false } },
                    { "workshopid", new ColumnDto { TableName = "sessions", ColumnName= "workshopid", IsPrimaryKey= false } },
                },
                GraphQlLookup = new Dictionary<string, ColumnDto> {
                    { "id", new ColumnDto { TableName = "sessions", ColumnName= "sid", IsPrimaryKey= true } },
                    { "status__code", new ColumnDto { TableName = "sessions", ColumnName= "status code", IsPrimaryKey= false } },
                    { "firstname", new ColumnDto { TableName = "sessions", ColumnName= "firstname", IsPrimaryKey= false } },
                    { "lastname", new ColumnDto { TableName = "sessions", ColumnName= "lastname", IsPrimaryKey= false } },
                    { "workshopid", new ColumnDto { TableName = "sessions", ColumnName= "workshopid", IsPrimaryKey= false } },
                },
            };
            workshops.MultiLinks.Add("sessions", new TableLinkDto
            {
                Name = "sessions",
                ParentTable = workshops,
                ParentId = workshops.ColumnLookup["id"],
                ChildTable = sessions,
                ChildId = sessions.ColumnLookup["workshopid"],
            });
            sessions.SingleLinks.Add("work__shops", new TableLinkDto
            {
                Name = "work shops",
                ParentTable = workshops,
                ParentId = workshops.ColumnLookup["id"],
                ChildTable = sessions,
                ChildId = sessions.ColumnLookup["workshopid"],
            });
            workshops.MultiLinks.Add("participants table", new TableLinkDto
            {
                Name = "participants table",
                ParentTable = workshops,
                ParentId = workshops.ColumnLookup["id"],
                ChildTable = sessions,
                ChildId = sessions.ColumnLookup["workshopid"],
            });
            participants.SingleLinks.Add("work__shops", new TableLinkDto
            {
                Name = "work shops",
                ParentTable = workshops,
                ParentId = workshops.ColumnLookup["id"],
                ChildTable = sessions,
                ChildId = sessions.ColumnLookup["workshopid"],
            });
            var tables = new List<TableDto>() { workshops, sessions, participants };
            return tables;
        }
    }
}
