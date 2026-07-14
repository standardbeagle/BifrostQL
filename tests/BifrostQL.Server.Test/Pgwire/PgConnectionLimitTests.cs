using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// Connection-limit admission tests for slice 5. A shared lock-free counter caps concurrent
    /// connections; the over-limit connection is refused cleanly with 53300 too_many_connections
    /// during startup (not a crash or a hang), and a freed slot is reusable.
    /// </summary>
    public sealed class PgConnectionLimitTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private static IReadOnlyList<IReadOnlyDictionary<string, object?>> NoRows()
            => Array.Empty<IReadOnlyDictionary<string, object?>>();

        [Fact]
        public async Task OverTheLimit_ConnectionIsRefusedWith53300_ThenFreedSlotIsReusable()
        {
            await using var harness = new PgWireTestHarness(PgWireTestHarness.UsersExecutor(NoRows(), out _), maxConnections: 2);

            // Fill the two available slots with live authenticated sessions.
            var first = await harness.OpenSessionAsync();
            await harness.OpenSessionAsync();
            await harness.WaitForConnectionCountAsync(2);

            // The third connection is admitted no further than startup: a clean 53300, then close.
            var third = await harness.ConnectAsync();
            await third.Client.SendStartupAsync("alice");
            var rejected = await third.Client.WaitForReadyOrErrorAsync().WaitAsync(Timeout);

            rejected.WasRejected.Should().BeTrue();
            rejected.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateTooManyConnections);

            // Free one slot; the counter is decremented on disconnect (finally).
            await first.DisposeAsync();
            await harness.WaitForConnectionCountAsync(1);

            // A brand-new connection is now admitted and reaches a ready session.
            var revived = await harness.OpenSessionAsync();
            revived.Client.BackendPid.Should().NotBe(0);
            await harness.WaitForConnectionCountAsync(2);
        }
    }
}
