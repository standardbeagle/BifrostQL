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

        /// <summary>
        /// Pins the subtype's ACCOUNT axis in isolation. The wire facts above set both caps equal
        /// on a single source, so a <see cref="RespAuthRateLimiter"/> that quietly stopped passing
        /// the account through (admitting with a null account, as pgwire legitimately does) would
        /// stay green on them: the source axis alone still refuses. Many sources hammering ONE
        /// login must trip the account cap while the per-source budget is nowhere near spent.
        /// </summary>
        [Fact]
        public void Per_account_cap_is_enforced_across_sources()
        {
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var limiter = new RespAuthRateLimiter(
                maxPerSource: 100, maxPerAccount: 2, window: Window, clock: () => now);

            limiter.TryAttempt("10.0.0.1", "victim").Should().BeTrue();
            limiter.TryAttempt("10.0.0.2", "victim").Should().BeTrue();
            limiter.TryAttempt("10.0.0.3", "victim").Should().BeFalse(
                "the per-account window is at its cap although no source has spent its budget");
            limiter.TryAttempt("10.0.0.3", "other").Should().BeTrue(
                "the account cap is per login, not per source: a different login from the same source is admitted");
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
                // The pre-auth deadline shares this test's clock, and the window rollover below
                // jumps past 30 seconds; a real budget here would close the connection for reasons
                // that have nothing to do with the limiter under test.
                AuthenticationTimeout = TimeSpan.FromHours(1),
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
