using System;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap;

/// <summary>
/// The bind rate-limiter's counter map is bounded on both axes (per source, per account)
/// AND bounded in SIZE: entries were never removed, so an account-spraying or IP-churning
/// peer would grow it without limit on the unauthenticated bind path. Pruning reclaims
/// rolled-over counters once the map is over a soft cap, never a live in-window counter.
/// </summary>
public class LdapBindRateLimiterTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Get() => Now;
    }

    [Fact]
    public void PerSourceCap_IsEnforced_ThenResetsAfterTheWindow()
    {
        var clock = new Clock();
        var limiter = new LdapBindRateLimiter(maxPerSource: 2, maxPerAccount: 100,
            window: TimeSpan.FromMinutes(1), clock: clock.Get);

        limiter.TryBind("1.2.3.4", "alice").Should().BeTrue();
        limiter.TryBind("1.2.3.4", "bob").Should().BeTrue();
        limiter.TryBind("1.2.3.4", "carol").Should().BeFalse("the per-source window is at its cap");

        clock.Now = clock.Now.AddMinutes(2); // window rolled over
        limiter.TryBind("1.2.3.4", "carol").Should().BeTrue("a new window admits again");
    }

    [Fact]
    public void PerAccountCap_IsEnforced_AcrossSources()
    {
        var clock = new Clock();
        var limiter = new LdapBindRateLimiter(maxPerSource: 100, maxPerAccount: 2,
            window: TimeSpan.FromMinutes(1), clock: clock.Get);

        limiter.TryBind("a", "victim").Should().BeTrue();
        limiter.TryBind("b", "victim").Should().BeTrue();
        limiter.TryBind("c", "victim").Should().BeFalse("the per-account window is at its cap");
    }

    [Fact]
    public void OverCap_ExpiredCounters_ArePruned()
    {
        var clock = new Clock();
        // Small cap so a handful of distinct accounts exceeds it.
        var limiter = new LdapBindRateLimiter(maxPerSource: 100, maxPerAccount: 100,
            window: TimeSpan.FromMinutes(1), clock: clock.Get, maxTrackedKeys: 4);

        // Each distinct (source, account) adds two counters (s: and a:). Three distinct pairs
        // create six counters, exceeding the cap of 4.
        for (var i = 0; i < 3; i++)
            limiter.TryBind($"src-{i}", $"acct-{i}");

        // Roll every window over, then do one more bind: the prune (over cap) reclaims the
        // six now-expired counters, so only the fresh pair's counters remain.
        clock.Now = clock.Now.AddMinutes(2);
        limiter.TryBind("src-fresh", "acct-fresh");

        limiter.TrackedKeyCount.Should().BeLessThanOrEqualTo(2,
            "an unbounded map is the bug; expired counters must be reclaimed once over the cap");
    }

    [Fact]
    public void SourceKey_DropsTheEphemeralPort_SoTheCapIsPerClientIp()
    {
        var a = LdapConnectionHandler.SourceKey(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("1.2.3.4"), 54321));
        var b = LdapConnectionHandler.SourceKey(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("1.2.3.4"), 12345));

        a.Should().Be("1.2.3.4");
        b.Should().Be("1.2.3.4");
        a.Should().Be(b, "two connections from one client IP must share the per-source rate-limit key");
    }

    [Fact]
    public void SourceKey_NullEndpoint_IsStableSentinel()
    {
        LdapConnectionHandler.SourceKey(null).Should().Be("unknown");
    }

    [Fact]
    public void OverCap_LiveCounters_AreNeverPruned()
    {
        var clock = new Clock();
        var limiter = new LdapBindRateLimiter(maxPerSource: 100, maxPerAccount: 100,
            window: TimeSpan.FromHours(1), clock: clock.Get, maxTrackedKeys: 2);

        // Six live (in-window) counters, well over the cap — none may be pruned, because a
        // live counter carries a real rate-limit decision.
        for (var i = 0; i < 3; i++)
            limiter.TryBind($"src-{i}", $"acct-{i}");

        limiter.TrackedKeyCount.Should().Be(6,
            "pruning must never drop a live in-window counter, even when over the cap");
    }
}
