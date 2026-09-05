using System;
using System.Linq;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using FluentAssertions;
using GraphQLParser;
using Xunit;

namespace BifrostQL.Core.QueryModel
{
    /// <summary>
    /// Finding M10 (operator half): the explicit <c>_join_&lt;table&gt;</c> /
    /// <c>_single_&lt;table&gt;</c> path forwards the client-supplied <c>on</c>
    /// operator to <c>dialect.GetOperator</c> unvalidated. The join vocabulary is
    /// <c>_eq</c>/<c>_neq</c> only; any other operator is a client shape fault and
    /// must surface as <see cref="BifrostExecutionError"/> whose text carries no
    /// caller-supplied identifier.
    /// </summary>
    public sealed class JoinOperatorGateTests
    {
        [Theory]
        [InlineData("_eq")]
        [InlineData("_neq")]
        public async Task ExplicitJoin_OnWithEqualityOperator_IsAccepted(string op)
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();
            var model = new DbModel { Tables = SqlVisitorToSqlTest.GetFakeTables() };
            var ast = Parser.Parse($"query {{ work__shops {{ data {{ id sess:_join_sessions(on: {{id: {{{op}: workshopid}}}}) {{ id }} }} }} }}");
            await visitor.VisitAsync(ast, ctx);
            ctx.GetFinalQueries(model).Should().ContainSingle();
        }

        [Theory]
        [InlineData("_gt")]
        [InlineData("_contains")]
        [InlineData("_in")]
        public async Task ExplicitJoin_OnWithNonEqualityOperator_ThrowsBifrostExecutionError(string op)
        {
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();
            var model = new DbModel { Tables = SqlVisitorToSqlTest.GetFakeTables() };
            var ast = Parser.Parse($"query {{ work__shops {{ data {{ id sess:_join_sessions(on: {{id: {{{op}: workshopid}}}}) {{ id }} }} }} }}");
            // ToJoin runs when the visited tree is materialized, not during VisitAsync.
            var act = async () =>
            {
                await visitor.VisitAsync(ast, ctx);
                ctx.GetFinalQueries(model);
            };
            var error = (await act.Should().ThrowAsync<BifrostExecutionError>()).Which;
            error.Message.Should().NotContain("workshopid", "a shape fault must not echo caller-supplied identifiers");
        }

        [Fact]
        public async Task ExplicitJoin_OnWithTwoColumns_ThrowsBifrostExecutionError()
        {
            // Previously raw ArgumentException — an unclassified fault escaping as a
            // different wire shape than every other join shape fault.
            var ctx = new SqlContext();
            var visitor = new SqlVisitor();
            var model = new DbModel { Tables = SqlVisitorToSqlTest.GetFakeTables() };
            var ast = Parser.Parse("query { work__shops { data { id sess:_join_sessions(on: {id: {_eq: workshopid} number: {_eq: status}}) { id } } } }");
            var act = async () =>
            {
                await visitor.VisitAsync(ast, ctx);
                ctx.GetFinalQueries(model);
            };
            await act.Should().ThrowAsync<BifrostExecutionError>();
        }
    }
}
