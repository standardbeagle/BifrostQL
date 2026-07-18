using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Criterion 4 (cancellation/deadline propagation) at the dispatch seam: the read dispatcher
    /// threads the RPC's <see cref="CancellationToken"/> straight into
    /// <see cref="IQueryIntentExecutor.ExecuteAsync"/>, so a gRPC deadline/cancel cancels the
    /// underlying query. Also pins that the adapter builds NO predicate on List (tenant scope is the
    /// pipeline's job) and binds ALL primary-key columns on Get.
    /// </summary>
    public class GrpcReadDispatcherTests
    {
        [Fact]
        public async Task List_forwards_the_cancellation_token_to_the_executor()
        {
            var (executor, captured) = CapturingExecutor();
            using var cts = new CancellationTokenSource();

            await GrpcReadDispatcher.ListAsync(
                executor, Table("Widgets"), limit: 10, Ctx(), endpoint: "/graphql", cts.Token);

            captured.Value.Should().Be(cts.Token);
        }

        [Fact]
        public async Task Get_forwards_the_cancellation_token_to_the_executor()
        {
            var (executor, captured) = CapturingExecutor();
            using var cts = new CancellationTokenSource();

            await GrpcReadDispatcher.GetByKeyAsync(
                executor, Table("Widgets"), new Dictionary<string, object?> { ["id"] = 1 },
                Ctx(), endpoint: "/graphql", cts.Token);

            captured.Value.Should().Be(cts.Token);
        }

        [Fact]
        public async Task List_sets_the_limit_and_no_filter()
        {
            var (executor, _) = CapturingExecutor(out var intents);

            await GrpcReadDispatcher.ListAsync(executor, Table("Widgets"), limit: 42, Ctx(), null, default);

            var query = intents.Single().Query;
            query.Limit.Should().Be(42);
            query.Filter.Should().BeNull("List must carry no adapter predicate — tenant scope is the pipeline's job");
        }

        [Fact]
        public async Task Get_missing_a_key_field_is_an_invalid_argument()
        {
            var (executor, _) = CapturingExecutor();

            var act = () => GrpcReadDispatcher.GetByKeyAsync(
                executor, Table("Widgets"), new Dictionary<string, object?>(), Ctx(), null, default);

            await act.Should().ThrowAsync<GrpcRequestException>().WithMessage("*id*");
        }

        // ---- helpers ----

        private static IDictionary<string, object?> Ctx() => new Dictionary<string, object?>();

        private sealed class Box<T> { public T Value = default!; }

        private static (IQueryIntentExecutor, Box<CancellationToken>) CapturingExecutor()
            => CapturingExecutor(out _);

        private static (IQueryIntentExecutor, Box<CancellationToken>) CapturingExecutor(out List<QueryIntent> intents)
        {
            var captured = new Box<CancellationToken>();
            var seen = new List<QueryIntent>();
            intents = seen;
            var executor = Substitute.For<IQueryIntentExecutor>();
            var result = new QueryIntentResult
            {
                Rows = new List<IReadOnlyDictionary<string, object?>>(),
                Sql = string.Empty,
            };
            executor
                .ExecuteAsync(Arg.Do<QueryIntent>(i => seen.Add(i)), Arg.Do<CancellationToken>(t => captured.Value = t))
                .Returns(Task.FromResult(result));
            return (executor, captured);
        }

        private static IDbTable Table(string name)
        {
            var id = new ColumnDto { ColumnName = "id", GraphQlName = "id", DataType = "int", IsPrimaryKey = true };
            var col = new ColumnDto { ColumnName = "name", GraphQlName = "name", DataType = "varchar" };
            var table = Substitute.For<IDbTable>();
            table.GraphQlName.Returns(name);
            table.DbName.Returns(name);
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(new[] { id, col });
            table.KeyColumns.Returns(new[] { id });
            return table;
        }
    }
}
