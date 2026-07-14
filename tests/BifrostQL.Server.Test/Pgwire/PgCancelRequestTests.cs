using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// CancelRequest tests for slice 5. A CancelRequest arrives on a SEPARATE, unauthenticated
    /// connection carrying the target's BackendKeyData (PID, secret). These drive the whole
    /// path over real sockets: the correct pair cooperatively aborts the target's in-flight
    /// query (session survives), while a wrong secret is a fail-closed no-op.
    /// </summary>
    public sealed class PgCancelRequestTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        [Fact]
        public async Task CorrectPidAndSecret_CancelsInFlightQuery_SessionSurvives()
        {
            var started = new TaskCompletionSource();
            var executor = PgWireTestHarness.BlockingExecutor(started);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            // Kick off a query that blocks until its cooperative token fires.
            await client.SendQueryAsync("SELECT id FROM users");
            await started.Task.WaitAsync(Timeout); // execution is underway

            // A matching CancelRequest on a second connection aborts it.
            await PgHandshakeClient.SendCancelRequestAsync(harness.Endpoint, client.BackendPid, client.BackendSecret);

            var result = await client.ReadQueryResultAsync().WaitAsync(Timeout);

            result.HasError.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateQueryCanceled);
            result.ErrorMessage.Should().Be(PgWireProtocol.QueryCanceledMessage);
            result.TransactionStatus.Should().Be('I'); // back to idle — the session lives on
        }

        [Fact]
        public async Task WrongSecret_DoesNotCancel_AndDoesNotErrorTheTarget()
        {
            var started = new TaskCompletionSource();
            var executor = PgWireTestHarness.BlockingExecutor(started);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            await client.SendQueryAsync("SELECT id FROM users");
            await started.Task.WaitAsync(Timeout);

            // Same PID, WRONG secret: fail-closed no-op — the query must keep running.
            await PgHandshakeClient.SendCancelRequestAsync(harness.Endpoint, client.BackendPid, client.BackendSecret + 1);

            // No response arrives: the target was neither canceled nor errored. A read times out
            // because the query is still blocked, proving the bogus cancel did nothing.
            var read = client.ReadQueryResultAsync();
            Func<Task> act = async () => await read.WaitAsync(TimeSpan.FromSeconds(1));
            await act.Should().ThrowAsync<TimeoutException>();
        }

        [Fact]
        public async Task UnknownPid_IsSilentlyIgnored()
        {
            var started = new TaskCompletionSource();
            var executor = PgWireTestHarness.BlockingExecutor(started);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            await client.SendQueryAsync("SELECT id FROM users");
            await started.Task.WaitAsync(Timeout);

            // A PID that was never registered must not disturb any session.
            await PgHandshakeClient.SendCancelRequestAsync(harness.Endpoint, pid: client.BackendPid + 9999, secret: 0);

            var read = client.ReadQueryResultAsync();
            Func<Task> act = async () => await read.WaitAsync(TimeSpan.FromSeconds(1));
            await act.Should().ThrowAsync<TimeoutException>();
        }
    }
}
