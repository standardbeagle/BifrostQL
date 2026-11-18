using System.Security.Claims;
using System.Text;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// A RESP peer that sends commands and never reads the replies parks the server's flush on a
    /// full window. The read deadline cannot see that — the loop is not reading — so before the
    /// write bound the slot was held for as long as the peer liked: past AuthenticationTimeout for
    /// an unauthenticated PING flood, past IdleTimeout for an authenticated large reply. Both facts
    /// run the production handler over the production Kestrel glue with a stalled peer, on an
    /// injected clock, and prove the connection ends and its admission slot is released.
    /// </summary>
    public sealed class RespWriteDeadlineTests
    {
        private static readonly TimeSpan PreAuth = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task Unauthenticated_ping_flood_into_a_zero_window_is_cut_at_the_pre_auth_deadline()
        {
            var clock = new FakeTimeProvider();
            var limiter = new RespConnectionLimiter(1);
            var options = new RespWireOptions
            {
                AllowCleartextAuth = true,
                AuthenticationTimeout = PreAuth,
                // Both far beyond the total advance below, so only the pre-auth deadline can end the write.
                WriteTimeout = TimeSpan.FromDays(1),
                IdleTimeout = TimeSpan.FromDays(1),
            };
            var handler = NewHandler(options, limiter, clock);
            var transport = new StalledReaderTransport();

            // Enough NOAUTH replies to overrun the pause threshold several times over.
            var ping = Encoding.ASCII.GetBytes("*1\r\n$4\r\nPING\r\n");
            await transport.SendAsync(Enumerable.Repeat(ping, 200).SelectMany(b => b).ToArray());

            var session = handler.HandleConnectionAsync(transport.ServerStream, CancellationToken.None);
            await transport.WaitUntilServerIsBlockedAsync().WaitAsync(TimeSpan.FromSeconds(10));
            session.IsCompleted.Should().BeFalse("the session is parked on the full window before the clock moves");
            limiter.Count.Should().Be(1, "the parked session still holds its admission slot");

            await AdvanceUntilDoneAsync(clock, session, PreAuth + TimeSpan.FromSeconds(1));

            session.IsCompleted.Should().BeTrue(
                "an unauthenticated session's writes must end with its pre-auth budget, not wait on a peer that never reads");
            session.IsCompletedSuccessfully.Should().BeTrue("a cut write closes the connection cleanly, never faults the handler");
            limiter.Count.Should().Be(0, "the session's slot is released when the write is cut");
        }

        [Fact]
        public async Task Authenticated_large_reply_to_a_stalled_reader_is_cut_at_the_write_timeout()
        {
            var clock = new FakeTimeProvider();
            var limiter = new RespConnectionLimiter(1);
            var writeTimeout = TimeSpan.FromMinutes(1);
            var options = new RespWireOptions
            {
                AllowCleartextAuth = true,
                AuthenticationTimeout = PreAuth,
                WriteTimeout = writeTimeout,
                // Far beyond the total advance below: only the write timeout may end this session.
                IdleTimeout = TimeSpan.FromDays(1),
            };
            var handler = NewHandler(options, limiter, clock);
            var transport = new StalledReaderTransport();

            var session = handler.HandleConnectionAsync(transport.ServerStream, CancellationToken.None);
            await transport.SendAsync(Encoding.ASCII.GetBytes("*3\r\n$4\r\nAUTH\r\n$5\r\nalice\r\n$6\r\ns3cret\r\n"));
            Encoding.ASCII.GetString(await transport.ReadAvailableAsync().WaitAsync(TimeSpan.FromSeconds(10)))
                .Should().Be("+OK\r\n");

            // One reply far larger than the window, which the peer never reads.
            var payload = new string('x', 64 * 1024);
            await transport.SendAsync(Encoding.ASCII.GetBytes($"*2\r\n$4\r\nECHO\r\n${payload.Length}\r\n{payload}\r\n"));
            await transport.WaitUntilServerIsBlockedAsync().WaitAsync(TimeSpan.FromSeconds(10));

            await AdvanceUntilDoneAsync(clock, session, writeTimeout + TimeSpan.FromSeconds(1));

            session.IsCompleted.Should().BeTrue(
                "an authenticated reply that stalls on a peer that never reads must end at WriteTimeout");
            session.IsCompletedSuccessfully.Should().BeTrue("a cut write closes the connection cleanly, never faults the handler");
            limiter.Count.Should().Be(0);
        }

        // Advances the injected clock and gives the cancelled flush a moment to unwind, per poll:
        // the clock decides expiry; the real wait only lets continuations run.
        private static async Task AdvanceUntilDoneAsync(FakeTimeProvider clock, Task session, TimeSpan step)
        {
            for (var poll = 0; poll < 50 && !session.IsCompleted; poll++)
            {
                clock.Advance(step);
                await Task.WhenAny(session, Task.Delay(TimeSpan.FromMilliseconds(100)));
            }
        }

        private static RespConnectionHandler NewHandler(RespWireOptions options, RespConnectionLimiter limiter, TimeProvider clock)
        {
            var store = new FakeRespCredentialStore().Add(
                "alice", "s3cret",
                new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "user-alice") }, "resp")));
            return new RespConnectionHandler(
                store, BifrostAuthContextFactory.Instance, RespFixture.EmptyServices(), options,
                dataHandlers: null, logger: null, connectionLimiter: limiter, timeProvider: clock);
        }
    }
}
