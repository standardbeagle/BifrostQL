using BifrostQL.Server.Resp;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// A front door with <c>RequireAuthentication = false</c> never authenticates anyone, so
    /// <c>session.IsAuthenticated</c> stays false for the life of the connection. The read deadline
    /// keyed off that flag alone, which meant every connection on an anonymous front door kept the
    /// 30-second PRE-AUTH budget and was dropped 30 seconds after connect no matter how busy it
    /// was — the deliberate anonymous opt-in made the port unusable rather than open.
    ///
    /// <para>The pre-auth budget exists to stop a silent peer holding an admission slot it never
    /// earned. Where no authentication is required there is no pre-auth phase to bound; the right
    /// budget is the idle timeout, which an active connection resets on every command. Virtual time
    /// drives it here — a real 30-second wait would make this a slow test for no extra proof.</para>
    /// </summary>
    public sealed class RespAnonymousDeadlineTests
    {
        [Fact]
        public async Task An_anonymous_session_survives_past_the_authentication_timeout()
        {
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var options = new RespWireOptions
            {
                RequireAuthentication = false,
                AuthenticationTimeout = TimeSpan.FromSeconds(30),
                IdleTimeout = TimeSpan.FromMinutes(10),
            };

            await using var fixture = await RespFixture.StartAsync(
                new FakeRespCredentialStore(), RespFixture.EmptyServices(), options, clock: () => now);

            await fixture.Client.SendCommandAsync("PING");
            (await ReplyAsync(fixture)).Should().BeOfType<RespSimpleString>()
                .Which.Value.Should().Be(RespProtocol.Pong);

            // Well past the 30-second pre-auth budget, but only 35 seconds into a 10-minute idle
            // window: a client that is actively issuing commands must stay connected.
            now += TimeSpan.FromSeconds(35);

            await fixture.Client.SendCommandAsync("PING");
            (await ReplyAsync(fixture)).Should().BeOfType<RespSimpleString>();

            // The second command is the one that proves it. The loop only re-reads the clock
            // between reads, so the command above can still land on a deadline computed before the
            // jump; this one cannot.
            await fixture.Client.SendCommandAsync("PING");
            (await ReplyAsync(fixture)).Should().BeOfType<RespSimpleString>()
                .Which.Value.Should().Be(RespProtocol.Pong,
                    "an anonymous front door has no pre-auth phase to bound, so its connections must "
                    + "live under the idle timeout rather than being dropped 30 seconds after connect");
        }

        private static Task<RespValue?> ReplyAsync(RespFixture fixture) =>
            fixture.Client.ReadReplyAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
