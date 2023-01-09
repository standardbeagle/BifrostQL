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

            var ast = Parser.Parse("query { workshops { id } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables().Select(t => t.ToSql(new DbModel { Tables = tables })).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "workshops:workshops", "SELECT [id] [id] FROM [workshops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "workshops:workshops_count", "SELECT COUNT(*) FROM [workshops]"},
                });

        }

        [Fact]
        public async Task SimpleJoinQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();
            var tables = new List<TableDto>();

            var ast = Parser.Parse("query { workshops { id sess:_join_sessions(on: [\"id\", \"workshopid\"]) { sid, status } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables().Select(t => t.ToSql(new DbModel { Tables = tables })).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "workshops:workshops", "SELECT [id] [id] FROM [workshops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "workshops:workshops_count", "SELECT COUNT(*) FROM [workshops]"},
                    { "sess+_join_sessions", "SELECT a.[JoinId] [src_id], b.[sid] AS [sid],b.[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [workshops]) a INNER JOIN [sessions] b ON a.[JoinId] = b.[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                });

        }

        [Fact]
        public async Task SimpleLinkQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();
            var workshops = new TableDto
            {
                TableSchema = "dbo",
                TableName = "workshops",
                GraphQLName = "workshops",
                ColumnLookup = new Dictionary<string, ColumnDto> {
                        { "id", new ColumnDto { TableName = "workshops", ColumnName= "id", IsPrimaryKey= true } },
                    },
            };
            var sessions = new TableDto
            {
                TableSchema = "dbo",
                TableName = "sessions",
                GraphQLName = "sessions",
                ColumnLookup = new Dictionary<string, ColumnDto> {
                        { "sid", new ColumnDto { TableName = "sessions", ColumnName= "sid", IsPrimaryKey= true } },
                        { "status", new ColumnDto { TableName = "sessions", ColumnName= "status", IsPrimaryKey= true } },
                        { "workshopid", new ColumnDto { TableName = "sessions", ColumnName= "workshopid", IsPrimaryKey= true } },
                    },
            };
            workshops.MultiLinks.Add(new TableLinkDto {  
                Name = "sessions",
                ParentTable = workshops,
                ChildTable = sessions,
                ChildId = sessions.ColumnLookup["workshopid"],
                ParentId = workshops.ColumnLookup["id"],
            });
            var tables = new List<TableDto>() { workshops, sessions };

            var ast = Parser.Parse("query { workshops { id sess:sessions { sid, status } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = ctx.GetFinalTables().Select(t => t.ToSql(new DbModel { Tables = tables })).ToArray();
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "workshops:workshops", "SELECT [id] [id] FROM [workshops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "workshops:workshops_count", "SELECT COUNT(*) FROM [workshops]"},
                    { "sess+workshops_sessions", "SELECT a.[JoinId] [src_id], b.[sid] AS [sid],b.[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [workshops]) a INNER JOIN [sessions] b ON a.[JoinId] = b.[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                });

        }
    }
}
