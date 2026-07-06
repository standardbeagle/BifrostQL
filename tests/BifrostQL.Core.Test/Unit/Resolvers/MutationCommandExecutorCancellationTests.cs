using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.SqlServer;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Proves <see cref="MutationCommandExecutor.RunInTransactionAsync"/> honours the
/// request cancellation token: it short-circuits before taking a connection when
/// the token is already signalled, threads the token into the connection open,
/// and surfaces an <see cref="OperationCanceledException"/> unwrapped (rather than
/// masking it as a database error). Before the fix the whole mutation path was
/// token-less.
/// </summary>
public sealed class MutationCommandExecutorCancellationTests
{
    private static (IDbConnFactory factory, DbConnection conn) MakeConnFactory()
    {
        var conn = Substitute.For<DbConnection>();
        var factory = Substitute.For<IDbConnFactory>();
        factory.GetConnection().Returns(conn);
        factory.Dialect.Returns(SqlServerDialect.Instance);
        return (factory, conn);
    }

    [Fact]
    public async Task PreCancelledToken_ThrowsBeforeTakingConnection()
    {
        var (factory, _) = MakeConnFactory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => MutationCommandExecutor.RunInTransactionAsync(
            factory, (_, _) => Task.CompletedTask, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        // The abort was observed before any connection was requested.
        factory.DidNotReceive().GetConnection();
    }

    [Fact]
    public async Task PassesRequestTokenToConnectionOpen()
    {
        var (factory, conn) = MakeConnFactory();
        using var cts = new CancellationTokenSource();
        var captured = CancellationToken.None;
        conn.OpenAsync(Arg.Any<CancellationToken>()).Returns(ci =>
        {
            captured = ci.Arg<CancellationToken>();
            throw new InvalidOperationException("stop here");
        });

        var act = () => MutationCommandExecutor.RunInTransactionAsync(
            factory, (_, _) => Task.CompletedTask, cts.Token);

        // The non-cancellation failure is wrapped as a BifrostExecutionError.
        await act.Should().ThrowAsync<BifrostExecutionError>();
        captured.Should().Be(cts.Token);
    }

    [Fact]
    public async Task CancelledOpen_SurfacesOperationCanceledUnwrapped()
    {
        var (factory, conn) = MakeConnFactory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        conn.OpenAsync(Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromCanceled(ci.Arg<CancellationToken>()));

        // The pre-check throws first here, but even reaching OpenAsync a canceled
        // task must not be re-wrapped into a BifrostExecutionError.
        var act = () => MutationCommandExecutor.RunInTransactionAsync(
            factory, (_, _) => Task.CompletedTask, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
