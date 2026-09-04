using System.Security.Claims;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// AUTH was unlimited: Redis deliberately keeps a connection usable after a failed AUTH so a
    /// client can retry, which on this front door meant one socket could try passwords forever, and
    /// a peer could reconnect for free besides. The LDAP bind path is already bounded on two axes
    /// for exactly this reason — a per-source cap for one client spraying many accounts, and a
    /// per-account cap for many clients hammering one login.
    ///
    /// <para>The refusal must name only the rate limit. Varying it by whether the account exists
    /// would rebuild the enumeration oracle the uniform WRONGPASS reply exists to prevent.</para>
    /// </summary>
    public sealed class RespAuthRateLimitTests
    {
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        [Fact]
        public async Task Repeated_failures_from_one_source_are_refused_once_the_cap_is_reached()
        {
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await using var fixture = await StartAsync(() => now, attemptsPerSource: 3);

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await fixture.Client.SendCommandAsync("AUTH", "alice", "wrong");
                Message(await ReplyAsync(fixture)).Should().Be(RespProtocol.WrongPassError);
            }

            // Past the cap the credential is not even resolved — the attempt is refused first, so a
            // sustained guessing loop costs the front door nothing.
            await fixture.Client.SendCommandAsync("AUTH", "alice", "wrong");
            Message(await ReplyAsync(fixture)).Should().Be(RespProtocol.AuthRateLimitedError);

            // The CORRECT password is refused too while the window holds: a limiter that admits a
            // hit is not a limiter.
            await fixture.Client.SendCommandAsync("AUTH", "alice", "s3cret");
            Message(await ReplyAsync(fixture)).Should().Be(RespProtocol.AuthRateLimitedError);

            // A rolled-over window admits attempts again.
            now += Window + TimeSpan.FromSeconds(1);
            await fixture.Client.SendCommandAsync("AUTH", "alice", "s3cret");
            (await ReplyAsync(fixture)).Should().BeOfType<RespSimpleString>()
                .Which.Value.Should().Be(RespProtocol.Ok);
        }

        [Fact]
        public async Task The_refusal_is_identical_for_a_known_and_an_unknown_account()
        {
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await using var fixture = await StartAsync(() => now, attemptsPerSource: 1);

            await fixture.Client.SendCommandAsync("AUTH", "alice", "wrong");
            Message(await ReplyAsync(fixture)).Should().Be(RespProtocol.WrongPassError);

            await fixture.Client.SendCommandAsync("AUTH", "alice", "wrong");
            var known = Message(await ReplyAsync(fixture));

            await fixture.Client.SendCommandAsync("AUTH", "nobody-at-all", "wrong");
            var unknown = Message(await ReplyAsync(fixture));

            known.Should().Be(unknown,
                "a rate-limit refusal that varies by account existence is an enumeration oracle");
            known.Should().Be(RespProtocol.AuthRateLimitedError);
        }

        // ---- fixtures --------------------------------------------------------

        private static Task<RespFixture> StartAsync(Func<DateTimeOffset> clock, int attemptsPerSource)
        {
            var store = new FakeRespCredentialStore().Add(
                "alice", "s3cret",
                new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "user-alice") }, "resp")));

            var options = new RespWireOptions
            {
                AllowCleartextAuth = true, // the loopback test transport is not TLS
                MaxAuthAttemptsPerSource = attemptsPerSource,
                MaxAuthAttemptsPerAccount = attemptsPerSource,
                AuthRateLimitWindow = Window,
            };

            return RespFixture.StartAsync(store, RespFixture.EmptyServices(), options, clock);
        }

        private static string Message(RespValue? reply) =>
            reply.Should().BeOfType<RespError>().Subject.Message;

        private static Task<RespValue?> ReplyAsync(RespFixture fixture) =>
            fixture.Client.ReadReplyAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
