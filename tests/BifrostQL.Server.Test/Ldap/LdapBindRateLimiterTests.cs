using System;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap;

/// <summary>
/// The bind rate-limiter's counter map is bounded on both axes (per source, per account)
/// AND HARD-bounded in SIZE: entries were never removed, so an account-spraying or IP-churning
/// peer would grow it without limit on the unauthenticated bind path (and re-scan it O(n) on
/// every attempt). An already-tracked counter now updates in place and is never evicted (a live
/// rate-limit decision can never be bypassed), a throttled sweep reclaims rolled-over counters,
/// and a brand-new counter past the cap is refused when no slot can be freed.
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
    public void OverCap_LiveFlood_MapStaysBounded_AndTrackedCounterStillCaps()
    {
        var clock = new Clock();
        // Per-account cap of 2, a tiny map cap, a long window so nothing rolls over.
        var limiter = new LdapBindRateLimiter(maxPerSource: 100, maxPerAccount: 2,
            window: TimeSpan.FromHours(1), clock: clock.Get, maxTrackedKeys: 4);

        // Bring a victim account to its per-account cap BEFORE the flood.
        limiter.TryBind("src", "victim").Should().BeTrue();
        limiter.TryBind("src", "victim").Should().BeTrue();
        limiter.TryBind("src", "victim").Should().BeFalse("the per-account window is at its cap");

        // Flood with many distinct live (source, account) pairs — far past the map cap.
        for (var i = 0; i < 50; i++)
            limiter.TryBind($"flood-src-{i}", $"flood-acct-{i}");

        limiter.TrackedKeyCount.Should().BeLessThanOrEqualTo(4,
            "a live-key flood must not grow the counter map past its hard cap (the unbounded-growth bug)");
        limiter.TryBind("src", "victim").Should().BeFalse(
            "a counter already tracked is never evicted to admit a new key, so its cap is never bypassed");
    }
}
