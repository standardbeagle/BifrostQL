using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Invariant 8b: <c>.Value</c> is not a count. Delete read its affected count from
    /// <see cref="MutationIntentResult.Value"/> on the strength of a comment saying the pipeline
    /// leaves <see cref="MutationIntentResult.AffectedRows"/> null for a delete; the intent executor
    /// now sets it. These facts give Value and AffectedRows different numbers so a dispatcher that
    /// reads the wrong one reports the wrong count.
    /// </summary>
    public sealed class GrpcMutationDispatcherCountTests
    {
        private static IDbTable WidgetTable()
        {
            var id = new ColumnDto { ColumnName = "id", GraphQlName = "id", DataType = "int", OrdinalPosition = 1, IsPrimaryKey = true };
            var table = Substitute.For<IDbTable>();
            table.DbName.Returns("widgets");
            table.KeyColumns.Returns(new[] { id });
            return table;
        }

        private static IMutationIntentExecutor Returning(MutationIntentResult result)
        {
            var executor = Substitute.For<IMutationIntentExecutor>();
            executor.ExecuteAsync(Arg.Any<MutationIntent>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(result));
            return executor;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(0)]
        public async Task Delete_reports_AffectedRows_not_Value(int affected)
        {
            var executor = Returning(new MutationIntentResult { Value = 42, AffectedRows = affected });

            var outcome = await GrpcMutationDispatcher.DeleteAsync(
                executor, WidgetTable(), new Dictionary<string, object?> { ["id"] = 7 },
                new Dictionary<string, object?>(), endpoint: null, CancellationToken.None);

            outcome.AffectedRows.Should().Be(affected);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(0)]
        public async Task Insert_reports_AffectedRows_not_a_constant(int affected)
        {
            var executor = Returning(new MutationIntentResult { Value = 42, AffectedRows = affected });

            var outcome = await GrpcMutationDispatcher.InsertAsync(
                executor, WidgetTable(), new Dictionary<string, object?> { ["name"] = "w" },
                new Dictionary<string, object?>(), endpoint: null, CancellationToken.None);

            outcome.AffectedRows.Should().Be(affected);
            outcome.ReturnedKey.Should().Be("42");
        }

        [Fact]
        public async Task Insert_with_no_count_reports_zero()
        {
            var executor = Returning(new MutationIntentResult { Value = 42, AffectedRows = null });

            var outcome = await GrpcMutationDispatcher.InsertAsync(
                executor, WidgetTable(), new Dictionary<string, object?> { ["name"] = "w" },
                new Dictionary<string, object?>(), endpoint: null, CancellationToken.None);

            outcome.AffectedRows.Should().Be(0);
        }

        [Fact]
        public async Task Delete_with_no_count_reports_zero()
        {
            // Null is not success (invariant 8b): a result with no count did not write.
            var executor = Returning(new MutationIntentResult { Value = 1, AffectedRows = null });

            var outcome = await GrpcMutationDispatcher.DeleteAsync(
                executor, WidgetTable(), new Dictionary<string, object?> { ["id"] = 7 },
                new Dictionary<string, object?>(), endpoint: null, CancellationToken.None);

            outcome.AffectedRows.Should().Be(0);
        }
    }
}
