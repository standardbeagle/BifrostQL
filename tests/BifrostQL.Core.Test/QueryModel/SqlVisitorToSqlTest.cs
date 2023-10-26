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
        private static Dictionary<string, string> GetSql(GqlObjectQuery query, IDbModel model)
        {
            Dictionary<string, string> sqls = new();
            query.AddSql(model, sqls);
            return sqls;
        }

        private static Dictionary<string, string>[] GetSqls(SqlContext ctx, IDbModel model)
        {
            return ctx.GetFinalQueries(model).Select(q => GetSql(q, model)).ToArray();
        }

        [Fact]
        public async Task SimpleQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { work__shops { data { id } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                });

        }

        [Fact(Skip = "Same table???")]
        public async Task SimpleCountQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { work__shops { data { id _agg(value: id operation: count) } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work__shops=>agg__agg", "SELECT Count([id]) [_agg] FROM [dbo].[work shops]"}
                });
        }

        [Fact]
        public async Task JoinCountQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { work__shops { data { id _agg(value: { sessions: id } operation: count) } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work__shops=>agg__agg", "SELECT [src].[srcId], Count([next].[sid]) [_agg] FROM (SELECT [work shops].[id] AS [joinId], [work shops].[id] AS [srcId] FROM [dbo].[work shops]) [src] INNER JOIN [dbo].[sessions] [next] ON [src].[joinId] = [next].[workshopid] GROUP BY [src].[srcId]"}
                });
        }

        [Fact]
        public async Task DoubleJoinAvgQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { work__shops { data { id _agg(value: { sessions: {entry: value } } operation: avg) } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work__shops=>agg__agg", "SELECT [src].[srcId], Avg([next].[value]) [_agg] FROM (SELECT [next].[id] AS [joinId], [src].[srcId] FROM (SELECT [work shops].[id] AS [joinId], [work shops].[id] AS [srcId] FROM [dbo].[work shops]) [src] INNER JOIN [dbo].[sessions] [next] ON [src].[joinId] = [next].[workshopid]) [src] INNER JOIN [dbo].[entry] [next] ON [src].[joinId] = [next].[session_id] GROUP BY [src].[srcId]"}
                });
        }

        [Fact]
        public async Task FilterDoubleJoinAvgQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { work__shops(filter: { id: { _eq: 1 } } ) { data { id _agg(value: { sessions: {entry: value } } operation: avg) } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] WHERE [work shops].[id] = '1' ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops] WHERE [work shops].[id] = '1'"},
                    { "work__shops=>agg__agg", "SELECT [src].[srcId], Avg([next].[value]) [_agg] FROM (SELECT [next].[id] AS [joinId], [src].[srcId] FROM (SELECT [work shops].[id] AS [joinId], [work shops].[id] AS [srcId] FROM [dbo].[work shops] WHERE [work shops].[id] = '1') [src] INNER JOIN [dbo].[sessions] [next] ON [src].[joinId] = [next].[workshopid]) [src] INNER JOIN [dbo].[entry] [next] ON [src].[joinId] = [next].[session_id] GROUP BY [src].[srcId]"}
                });
        }

        [Fact]
        public async Task AliasCountQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { work__shops { data { id ct: _agg(value: {sessions: id} operation: count) } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work__shops=>agg_ct", "SELECT [src].[srcId], Count([next].[sid]) [ct] FROM (SELECT [work shops].[id] AS [joinId], [work shops].[id] AS [srcId] FROM [dbo].[work shops]) [src] INNER JOIN [dbo].[sessions] [next] ON [src].[joinId] = [next].[workshopid] GROUP BY [src].[srcId]"}
                });

        }

        [Fact]
        public async Task MultipleAggregateQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { work__shops { data { id ct: _agg(value: {sessions: id} operation: count) sum: _agg(value: {sessions: id} operation: sum) } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work__shops=>agg_ct", "SELECT [src].[srcId], Count([next].[sid]) [ct] FROM (SELECT [work shops].[id] AS [joinId], [work shops].[id] AS [srcId] FROM [dbo].[work shops]) [src] INNER JOIN [dbo].[sessions] [next] ON [src].[joinId] = [next].[workshopid] GROUP BY [src].[srcId]"},
                    { "work__shops=>agg_sum", "SELECT [src].[srcId], Sum([next].[sid]) [sum] FROM (SELECT [work shops].[id] AS [joinId], [work shops].[id] AS [srcId] FROM [dbo].[work shops]) [src] INNER JOIN [dbo].[sessions] [next] ON [src].[joinId] = [next].[workshopid] GROUP BY [src].[srcId]"}
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
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id],[percentage%] [percentage_34] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
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
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work__shops->sess", "SELECT [a].[JoinId] [src_id], [b].[sid] AS [id],[b].[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [work shops]) [a] INNER JOIN [sessions] [b] ON [a].[JoinId] != [b].[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                });

        }

        [Fact(Skip = "aggregate still beta")]
        public async Task SimpleAggQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { __agg_work__shops(operation: count value:id)");
            await visitor.VisitAsync(ast, ctx);
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work__shops->sess", "SELECT [a].[JoinId] [src_id], [b].[sid] AS [id],[b].[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [work shops]) [a] INNER JOIN [sessions] [b] ON [a].[JoinId] != [b].[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                });

        }

        [Fact(Skip = "aggregate still beta")]
        public async Task SimpleAggAndJoinQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { __agg_work__shops(operation: count value:id) work__shops { data { id sess:_join_sessions(on: {id: {_neq: workshopid}}) { id status } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work__shops->sess", "SELECT [a].[JoinId] [src_id], [b].[sid] AS [id],[b].[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [work shops]) [a] INNER JOIN [sessions] [b] ON [a].[JoinId] != [b].[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
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
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work__shops->sess", "SELECT [a].[JoinId] [src_id], [b].[sid] AS [id],[b].[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [work shops]) [a] INNER JOIN [sessions] [b] ON [a].[JoinId] != [b].[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
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
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work__shops->sess", "SELECT [a].[JoinId] [src_id], [b].[sid] AS [id],[b].[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [work shops]) [a] INNER JOIN [sessions] [b] ON [a].[JoinId] = [b].[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
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
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]"},
                    { "work__shops->sess", "SELECT [a].[JoinId] [src_id], [b].[sid] AS [id],[b].[status] AS [status] FROM (SELECT DISTINCT [id] AS JoinId FROM [work shops]) [a] INNER JOIN [sessions] [b] ON [a].[JoinId] = [b].[workshopid] WHERE [b].[status] = '0' ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
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
            var sqls = GetSqls(ctx, model);
            sqls.Should().HaveCount(3);
            sqls[0].Should().Equal(new Dictionary<string, string>
            {
                { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]" },
            });
            sqls[1].Should().Equal(new Dictionary<string, string>
            {
                { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops]" },
            });
            sqls[2].Should().Equal(new Dictionary<string, string>
            {
                { "work__shops", "SELECT [id] [id] FROM [dbo].[work shops] WHERE [work shops].[id] = '1' ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                { "work__shops=>count", "SELECT COUNT(*) FROM [dbo].[work shops] WHERE [work shops].[id] = '1'" },
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
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "sessions", "SELECT [sid] [id],[workshopid] [workshopid] FROM [dbo].[sessions] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "sessions=>count", "SELECT COUNT(*) FROM [dbo].[sessions]"},
                    { "sessions->work__shops", "SELECT [a].[JoinId] [src_id], [b].[id] AS [id],[b].[number] AS [number] FROM (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions]) [a] INNER JOIN [work shops] [b] ON [a].[JoinId] = [b].[id]" },
                });
        }

        [Fact]
        public async Task AliasSingleLinkQueryException()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { sessions { data { id shops: work__shops { id number } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "sessions", "SELECT [sid] [id],[workshopid] [workshopid] FROM [dbo].[sessions] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "sessions=>count", "SELECT COUNT(*) FROM [dbo].[sessions]"},
                    { "sessions->shops", "SELECT [a].[JoinId] [src_id], [b].[id] AS [id],[b].[number] AS [number] FROM (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions]) [a] INNER JOIN [work shops] [b] ON [a].[JoinId] = [b].[id]" },
                });
        }

        [Fact(Skip="implement paging on linked tables.")]
        public async Task PageBaseQueryException()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { sessions(offset: 3 limit: 2) { data { id shops: work__shops { id number } } } }");
            await visitor.VisitAsync(ast, ctx);
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "sessions", "SELECT [sid] [id],[workshopid] [workshopid] FROM [dbo].[sessions] ORDER BY (SELECT NULL) OFFSET 3 ROWS FETCH NEXT 2 ROWS ONLY"},
                    { "sessions=>count", "SELECT COUNT(*) FROM [dbo].[sessions]"},
                    { "sessions->shops", "SELECT [a].[JoinId] [src_id], [b].[id] AS [id],[b].[number] AS [number] FROM (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions] ORDER BY (SELECT NULL) OFFSET 3 ROWS FETCH NEXT 2 ROWS ONLY) [a] INNER JOIN [work shops] [b] ON [a].[JoinId] = [b].[id]" },
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
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "sessions", "SELECT [sid] [id],[workshopid] [workshopid] FROM [dbo].[sessions] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "sessions=>count", "SELECT COUNT(*) FROM [dbo].[sessions]"},
                    { "sessions->work__shops", "SELECT [a].[JoinId] [src_id], [b].[id] AS [id],[b].[number] AS [number] FROM (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions]) [a] INNER JOIN [work shops] [b] ON [a].[JoinId] = [b].[id]" },
                });
        }

        [Fact]
        public async Task SimpleDoubleLinkQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { sessions { data { id work__shops { id number participants__table { id firstname } } } } }");
            await visitor.VisitAsync(ast, ctx);
            var gqlObjectQueries = ctx.GetFinalQueries(model);
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "sessions", "SELECT [sid] [id],[workshopid] [workshopid] FROM [dbo].[sessions] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "sessions=>count", "SELECT COUNT(*) FROM [dbo].[sessions]"},
                    { "sessions->work__shops", "SELECT [a].[JoinId] [src_id], [b].[id] AS [id],[b].[number] AS [number] FROM (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions]) [a] INNER JOIN [work shops] [b] ON [a].[JoinId] = [b].[id]" },
                    { "sessions->work__shops->participants__table", "SELECT [a].[JoinId] [src_id], [b].[sid] AS [id],[b].[firstname] AS [firstname] FROM (SELECT DISTINCT [a].[id] AS JoinId FROM [work shops] [a] INNER JOIN (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions]) [b] ON [b].[JoinId] = [a].[id]) [a] INNER JOIN [participants table] [b] ON [a].[JoinId] = [b].[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
                });
        }

        [Fact]
        public async Task FilterDoubleLinkQuerySuccess()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();

            var model = new DbModel { Tables = GetFakeTables() };
            var ast = Parser.Parse("query { sessions(filter: { id: {_eq: 1}}) { data { id work__shops { id number participants__table { id firstname } } } } }");
            await visitor.VisitAsync(ast, ctx);
            var gqlObjectQueries = ctx.GetFinalQueries(model);
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "sessions", "SELECT [sid] [id],[workshopid] [workshopid] FROM [dbo].[sessions] WHERE [sessions].[sid] = '1' ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "sessions=>count", "SELECT COUNT(*) FROM [dbo].[sessions] WHERE [sessions].[sid] = '1'"},
                    { "sessions->work__shops", "SELECT [a].[JoinId] [src_id], [b].[id] AS [id],[b].[number] AS [number] FROM (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions] WHERE [sessions].[sid] = '1') [a] INNER JOIN [work shops] [b] ON [a].[JoinId] = [b].[id]" },
                    { "sessions->work__shops->participants__table", "SELECT [a].[JoinId] [src_id], [b].[sid] AS [id],[b].[firstname] AS [firstname] FROM (SELECT DISTINCT [a].[id] AS JoinId FROM [work shops] [a] INNER JOIN (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions] WHERE [sessions].[sid] = '1') [b] ON [b].[JoinId] = [a].[id]) [a] INNER JOIN [participants table] [b] ON [a].[JoinId] = [b].[workshopid] ORDER BY (SELECT NULL) OFFSET 0 ROWS" },
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
            var sqls = GetSqls(ctx, model);
            sqls.Should().ContainSingle()
                .Which.Should().Equal(new Dictionary<string, string> {
                    { "sessions", "SELECT [sid] [id],[workshopid] [workshopid] FROM [dbo].[sessions] ORDER BY (SELECT NULL) OFFSET 0 ROWS"},
                    { "sessions=>count", "SELECT COUNT(*) FROM [dbo].[sessions]"},
                    { "sessions->workshop", "SELECT [a].[JoinId] [src_id], [b].[id] AS [id],[b].[number] AS [number] FROM (SELECT DISTINCT [workshopid] AS JoinId FROM [sessions]) [a] INNER JOIN [work shops] [b] ON [a].[JoinId] = [b].[id]" },
                });

        }

        public static List<DbTable> GetFakeTables()
        {
            var workshops = new DbTable
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
            var sessions = new DbTable
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
            var participants = new DbTable
            {
                TableSchema = "dbo",
                DbName = "participants table",
                GraphQlName = "participants__table",
                ColumnLookup = new Dictionary<string, ColumnDto> {
                    { "id", new ColumnDto { TableName = "participants table", ColumnName= "sid", IsPrimaryKey= true } },
                    { "status code", new ColumnDto { TableName = "participants table", ColumnName= "status code", IsPrimaryKey= false } },
                    { "firstname", new ColumnDto { TableName = "participants table", ColumnName= "firstname", IsPrimaryKey= false } },
                    { "lastname", new ColumnDto { TableName = "participants table", ColumnName= "lastname", IsPrimaryKey= false } },
                    { "workshopid", new ColumnDto { TableName = "participants table", ColumnName= "workshopid", IsPrimaryKey= false } },
                },
                GraphQlLookup = new Dictionary<string, ColumnDto> {
                    { "id", new ColumnDto { TableName = "participants table", ColumnName= "sid", IsPrimaryKey= true } },
                    { "status__code", new ColumnDto { TableName = "participants table", ColumnName= "status code", IsPrimaryKey= false } },
                    { "firstname", new ColumnDto { TableName = "participants table", ColumnName= "firstname", IsPrimaryKey= false } },
                    { "lastname", new ColumnDto { TableName = "participants table", ColumnName= "lastname", IsPrimaryKey= false } },
                    { "workshopid", new ColumnDto { TableName = "participants table", ColumnName= "workshopid", IsPrimaryKey= false } },
                },
            };
            var entries = new DbTable
            {
                TableSchema = "dbo",
                DbName = "entry",
                GraphQlName = "entry",
                ColumnLookup = new Dictionary<string, ColumnDto> {
                    { "id", new ColumnDto { TableName = "entry", ColumnName= "id", IsPrimaryKey= true } },
                    { "value", new ColumnDto { TableName = "entry", ColumnName= "value", IsPrimaryKey= false } },
                    { "session_id", new ColumnDto { TableName = "entry", ColumnName= "session_id", IsPrimaryKey= false } },
                },
                GraphQlLookup = new Dictionary<string, ColumnDto> {
                    { "id", new ColumnDto { TableName = "entry", ColumnName= "sid", IsPrimaryKey= true } },
                    { "value", new ColumnDto { TableName = "entry", ColumnName= "value", IsPrimaryKey= false } },
                    { "session_id", new ColumnDto { TableName = "entry", ColumnName= "session_id", IsPrimaryKey= false } },
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
            workshops.MultiLinks.Add("participants__table", new TableLinkDto
            {
                Name = "participants table",
                ParentTable = workshops,
                ParentId = workshops.ColumnLookup["id"],
                ChildTable = participants,
                ChildId = participants.ColumnLookup["workshopid"],
            });
            participants.SingleLinks.Add("work__shops", new TableLinkDto
            {
                Name = "work shops",
                ParentTable = workshops,
                ParentId = workshops.ColumnLookup["id"],
                ChildTable = participants,
                ChildId = participants.ColumnLookup["workshopid"],
            });
            sessions.MultiLinks.Add("entry", new TableLinkDto
            {
                Name = "entry",
                ParentTable = sessions,
                ParentId = sessions.ColumnLookup["id"],
                ChildTable = entries,
                ChildId = entries.ColumnLookup["session_id"],
            });
            entries.SingleLinks.Add("sessions", new TableLinkDto
            {
                Name = "sessions",
                ParentTable = sessions,
                ParentId = sessions.ColumnLookup["id"],
                ChildTable = entries,
                ChildId = entries.ColumnLookup["session_id"],
            });
            var tables = new List<DbTable>() { workshops, sessions, participants };
            return tables;
        }
    }
}
