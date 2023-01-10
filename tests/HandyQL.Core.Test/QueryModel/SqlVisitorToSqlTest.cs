using FluentAssertions;
using GraphQLParser;
using GraphQLProxy.Core.QueryModel;
using GraphQLProxy.Model;
using GraphQLProxy.QueryModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HandyQL.Core.QueryModel
{
    public sealed class SqlVisitorToSqlTest
    {
        [Fact]
        public async Task SimpleQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();
            var tables = new List<TableDto>();

            var ast = Parser.Parse("query { workshops { data { id } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables().Select(t => t.ToSql(new DbModel { Tables = tables })).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "workshops", "SELECT [id] [id] FROM [workshops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "workshops_count", "SELECT COUNT(*) FROM [workshops]"},
                });

        }

        [Fact]
        public async Task SimpleJoinQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();
            var tables = new List<TableDto>();

            var ast = Parser.Parse("query { workshops { data { id sess:_join_sessions(on: [\"id\", \"workshopid\"]) { sid status } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables().Select(t => t.ToSql(new DbModel { Tables = tables })).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "workshops", "SELECT [id] [id] FROM [workshops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "workshops_count", "SELECT COUNT(*) FROM [workshops]"},
                    { "workshops->sess", "SELECT a.[JoinId] [src_id], b.[sid] AS [sid],b.[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [workshops]) a INNER JOIN [sessions] b ON a.[JoinId] = b.[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                });

        }

        [Fact]
        public async Task SimpleLinkQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();
            List<TableDto> tables = GetFakeTables();

            var ast = Parser.Parse("query { workshops { data { id sess:sessions { sid status } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables().Select(t => t.ToSql(new DbModel { Tables = tables })).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "workshops", "SELECT [id] [id] FROM [workshops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "workshops_count", "SELECT COUNT(*) FROM [workshops]"},
                    { "workshops->sess", "SELECT a.[JoinId] [src_id], b.[sid] AS [sid],b.[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [workshops]) a INNER JOIN [sessions] b ON a.[JoinId] = b.[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                });

        }

        [Fact]
        public async Task SimpleSingleLinkQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();
            List<TableDto> tables = GetFakeTables();

            var ast = Parser.Parse("query { sessions { data { id workshop { id number } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables().Select(t => t.ToSql(new DbModel { Tables = tables })).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "sessions", "SELECT [id] [id],[workshopid] [workshopid] FROM [sessions] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "sessions_count", "SELECT COUNT(*) FROM [sessions]"},
                    { "sessions->workshop", "SELECT a.[JoinId] [src_id], b.[id] AS [id],b.[number] AS [number] FROM (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions]) a INNER JOIN [workshops] b ON a.[JoinId] = b.[id]" },
                });
        }
        [Fact]
        public async Task SimpleDynamicSingleLinkQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();
            List<TableDto> tables = GetFakeTables();

            var ast = Parser.Parse("query { sessions { data { id workshop: _single_Workshops(on: [\"workshopid\", \"id\"]) { id number } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables().Select(t => t.ToSql(new DbModel { Tables = tables })).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "sessions", "SELECT [id] [id],[workshopid] [workshopid] FROM [sessions] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "sessions_count", "SELECT COUNT(*) FROM [sessions]"},
                    { "sessions->workshop", "SELECT a.[JoinId] [src_id], b.[id] AS [id],b.[number] AS [number] FROM (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions]) a INNER JOIN [Workshops] b ON a.[JoinId] = b.[id]" },
                });

        }

        private static List<TableDto> GetFakeTables()
        {
            var workshops = new TableDto
            {
                TableSchema = "dbo",
                TableName = "workshops",
                GraphQLName = "workshops",
                ColumnLookup = new Dictionary<string, ColumnDto> {
                        { "id", new ColumnDto { TableName = "workshops", ColumnName= "id", IsPrimaryKey= true } },
                        { "number", new ColumnDto { TableName = "workshops", ColumnName= "number", IsPrimaryKey= true } },
                    },
            };
            var sessions = new TableDto
            {
                TableSchema = "dbo",
                TableName = "sessions",
                GraphQLName = "sessions",
                ColumnLookup = new Dictionary<string, ColumnDto> {
                        { "id", new ColumnDto { TableName = "sessions", ColumnName= "sid", IsPrimaryKey= true } },
                        { "status", new ColumnDto { TableName = "sessions", ColumnName= "status", IsPrimaryKey= true } },
                        { "workshopid", new ColumnDto { TableName = "sessions", ColumnName= "workshopid", IsPrimaryKey= true } },
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
            sessions.SingleLinks.Add("workshop", new TableLinkDto
            {
                Name = "workshop",
                ParentTable = workshops,
                ParentId = workshops.ColumnLookup["id"],
                ChildTable = sessions,
                ChildId = sessions.ColumnLookup["workshopid"],
            });
            var tables = new List<TableDto>() { workshops, sessions };
            return tables;
        }
    }
}
