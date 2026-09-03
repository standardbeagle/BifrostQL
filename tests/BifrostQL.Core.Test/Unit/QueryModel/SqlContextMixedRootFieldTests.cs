using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using GraphQLParser;

namespace BifrostQL.Core.QueryModel
{
    /// <summary>
    /// A GraphQL document may mix a table row query with root fields that are owned by
    /// their own resolvers — introspection, the aggregate/pivot/history surfaces, the raw
    /// query escape hatch and the schema field. The SQL context builds one
    /// <see cref="GqlObjectQuery"/> per collected root field, so before this pin any such
    /// sibling failed the whole document: the table root field resolved to null.
    /// </summary>
    public sealed class SqlContextMixedRootFieldTests
    {
        private static IDbModel GetFakeModel() => new DbModel { Tables = SqlVisitorToSqlTest.GetFakeTables() };

        [Theory]
        // The six non-table root field kinds a real schema exposes alongside table queries.
        [InlineData("__typename")]
        [InlineData("work__shopsAggregate(groupBy: [id]) { id _count }")]
        [InlineData("work__shopsPivot(rows: [id], columns: [number], aggregate: Count) { rows }")]
        [InlineData("work__shopsHistory { data { id } }")]
        [InlineData("_rawQuery(sql: \"select 1\")")]
        [InlineData("_dbSchema { graphQlName }")]
        public async Task NonTableRootField_LeavesSiblingTableQueryIntact(string siblingRootField)
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();
            var ast = Parser.Parse($"query {{ work__shops {{ data {{ id }} }} {siblingRootField} }}");
            await visitor.VisitAsync(ast, ctx);

            var queries = ctx.GetFinalQueries(GetFakeModel());

            // The non-table sibling is skipped, not thrown on, and the table field still
            // produces its query — the field that used to come back null.
            queries.Should().ContainSingle()
                .Which.GraphQlName.Should().Be("work__shops");
        }

        [Fact]
        public async Task NonTableRootField_BeforeTheTableField_LeavesItIntact()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();
            var ast = Parser.Parse("query { _dbSchema { graphQlName } work__shops { data { id } } }");
            await visitor.VisitAsync(ast, ctx);

            var queries = ctx.GetFinalQueries(GetFakeModel());

            queries.Should().ContainSingle()
                .Which.GraphQlName.Should().Be("work__shops");
        }

        [Fact]
        public async Task UnresolvableNestedField_FailsAsSanitizedExecutionError()
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();
            var ast = Parser.Parse("query { work__shops { data { not_a_table { id } } } }");
            await visitor.VisitAsync(ast, ctx);

            // A client-shape fault must not escape as ArgumentOutOfRangeException, and the
            // wire text must not echo the caller-supplied identifier back.
            var act = () => ctx.GetFinalQueries(GetFakeModel());
            act.Should().Throw<BifrostExecutionError>()
                .Which.Message.Should().NotContain("not_a_table");
        }
    }
}
