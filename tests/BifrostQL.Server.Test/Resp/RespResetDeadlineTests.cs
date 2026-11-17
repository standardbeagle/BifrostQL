using System.Security.Claims;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// A successful AUTH retires the pre-auth deadline. RESET then drops the session back to
    /// anonymous, and that credentialed→anonymous edge must arm a fresh window (invariant 15):
    /// without one an anonymous read has no timeout at all, so a peer that authenticated once
    /// could RESET, go silent and hold its admission slot forever.
    /// </summary>
    public sealed class RespResetDeadlineTests
    {
        private static readonly TimeSpan PreAuth = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task A_silent_connection_after_RESET_is_dropped_by_a_fresh_pre_auth_window()
        {
            var clock = new FakeTimeProvider();
            await using var fixture = await StartAsync(clock);

            await fixture.Client.SendCommandAsync("AUTH", "alice", "s3cret");
            (await ReplyAsync(fixture)).Should().BeOfType<RespSimpleString>()
                .Which.Value.Should().Be(RespProtocol.Ok);

            await fixture.Client.SendCommandAsync("RESET");
            (await ReplyAsync(fixture)).Should().BeOfType<RespSimpleString>()
                .Which.Value.Should().Be(RespProtocol.ResetReply);

            // Control: still connected right after RESET (the anonymous session answers NOAUTH).
            await fixture.Client.SendCommandAsync("PING");
            (await ReplyAsync(fixture)).Should().BeOfType<RespError>();

            // Go silent. The loop arms its read timer after it writes the reply, so advance per
            // poll: each step jumps past the window and fires whatever timer is pending by then.
            var read = fixture.Client.ReadReplyAsync();
            for (var poll = 0; poll < 100 && !read.IsCompleted; poll++)
            {
                clock.Advance(PreAuth + TimeSpan.FromSeconds(1));
                await Task.WhenAny(read, Task.Delay(TimeSpan.FromMilliseconds(100)));
            }

            read.IsCompleted.Should().BeTrue(
                "a session RESET back to anonymous must be bounded by a fresh pre-auth window, "
                + "not read with no timeout because it was once authenticated");
            (await read).Should().BeNull("the server closed the connection when the window expired");
        }

        private static Task<RespFixture> StartAsync(TimeProvider clock)
        {
            var store = new FakeRespCredentialStore().Add(
                "alice", "s3cret",
                new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "user-alice") }, "resp")));
            var options = new RespWireOptions
            {
                AllowCleartextAuth = true, // the loopback test transport is not TLS
                AuthenticationTimeout = PreAuth,
                // Far beyond the polls' total advance, so only the pre-auth window can close it.
                IdleTimeout = TimeSpan.FromDays(1),
            };
            return RespFixture.StartAsync(store, RespFixture.EmptyServices(), options, clock);
        }

        private static Task<RespValue?> ReplyAsync(RespFixture fixture) =>
            fixture.Client.ReadReplyAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
