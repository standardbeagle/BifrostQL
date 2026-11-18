using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// An authenticated peer that sends a query and never reads the result used to hold its
    /// connection slot forever: past ReadyForQuery nothing bounded the server's blocked write.
    /// <see cref="BifrostQL.Server.Pgwire.PgWireOptions.WriteTimeout"/> closes such a session.
    /// </summary>
    public sealed class PgWriteTimeoutTests
    {
        [Fact]
        public async Task Authenticated_peer_that_stops_reading_is_closed_by_the_write_timeout_and_frees_its_slot()
        {
            // The timeout is far beyond the harness's 5 s wait, so only advancing the injected clock
            // can end the session; a handler timing writes on the system clock (or not at all)
            // keeps the slot and fails the wait.
            var clock = new FakeTimeProvider();
            // One row far larger than loopback socket buffers, so the server's write blocks for good
            // once the unread receive window fills.
            var rows = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["id"] = 1, ["name"] = new string('x', 64 * 1024 * 1024), ["active"] = true },
            };
            await using var harness = new PgWireTestHarness(
                PgWireTestHarness.UsersExecutor(rows, out _),
                maxConnections: 1,
                timeProvider: clock,
                writeTimeout: TimeSpan.FromMinutes(10));

            var stalled = await harness.OpenSessionAsync();
            await stalled.Client.SendQueryAsync("SELECT id, name FROM users");
            await harness.WaitForConnectionCountAsync(1);

            // Never read. The write deadline must close the session and release the only slot.
            await harness.WaitForConnectionCountAsync(0,
                beforeEachPoll: () => clock.Advance(TimeSpan.FromMinutes(11)));

            // ...so a well-behaved client gets in.
            var next = await harness.OpenSessionAsync();
            next.Client.BackendPid.Should().NotBe(0);

            await stalled.DisposeAsync();
        }
    }
}
