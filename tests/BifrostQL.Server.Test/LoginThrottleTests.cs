using System;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The login throttle's failure map only ever removed an entry on a SUCCESSFUL login
/// for that exact key, so a peer rotating login strings or source IPs would grow it
/// without bound on the unauthenticated path (and re-scan it O(n) on every failure). It
/// is now HARD-bounded: an already-tracked key updates in place and is never evicted (a
/// victim's lockout can never be bypassed), a throttled sweep reclaims elapsed entries, and
/// a brand-new key past the cap is refused when no slot can be freed — memory stays bounded.
/// </summary>
public class LoginThrottleTests
{
    private static LocalAuthOptions Options(int max, TimeSpan window) =>
        new() { MaxFailedLoginAttempts = max, LockoutWindow = window };

    [Fact]
    public void OverCap_ElapsedEntries_ArePruned()
    {
        // Zero window ⇒ every entry is instantly elapsed, so once the cap is exceeded the
        // prune reclaims them all. Deterministic (no timing dependence).
        var throttle = new LocalAuthEndpoint.LoginThrottle(maxTrackedKeys: 2);
        var options = Options(max: 5, window: TimeSpan.Zero);

        throttle.RecordFailure("a", options);
        throttle.RecordFailure("b", options);
        throttle.TrackedKeyCount.Should().Be(2, "under the cap, nothing is pruned");

        throttle.RecordFailure("c", options); // count hits 3 > cap ⇒ prune runs

        throttle.TrackedKeyCount.Should().BeLessThanOrEqualTo(2,
            "an unbounded map is the bug; elapsed entries must be reclaimed once over the cap");
    }

    [Fact]
    public void OverCap_LiveFlood_MapStaysBounded_AndTrackedKeyStaysLockedOut()
    {
        // Regression: the map was only ever pruned of ELAPSED entries, so a flood of distinct
        // LIVE keys grew it without bound (and re-scanned it O(n) on every failure). The map must
        // now stay HARD-bounded at the cap under a live-key flood — while a key already tracked
        // (already being brute-forced) is never evicted to make room, so its lockout survives.
        var throttle = new LocalAuthEndpoint.LoginThrottle(maxTrackedKeys: 2);
        var options = Options(max: 2, window: TimeSpan.FromHours(1));

        // Lock out a victim BEFORE the flood (two failures reach the lockout threshold of 2).
        throttle.RecordFailure("victim", options);
        throttle.RecordFailure("victim", options);
        throttle.IsLockedOut("victim", options).Should().BeTrue();

        // Flood with many distinct live keys — far past the hard cap.
        for (var i = 0; i < 50; i++)
            throttle.RecordFailure($"flood-{i}", options);

        throttle.TrackedKeyCount.Should().BeLessThanOrEqualTo(2,
            "a live-key flood must not grow the map past its hard cap (the unbounded-growth bug)");
        throttle.IsLockedOut("victim", options).Should().BeTrue(
            "a key already tracked is never evicted to admit a new one, so its lockout is never bypassed");
    }

    [Fact]
    public void SuccessfulLogin_ClearsTheKey()
    {
        var throttle = new LocalAuthEndpoint.LoginThrottle(maxTrackedKeys: 100);
        var options = Options(max: 3, window: TimeSpan.FromMinutes(5));

        throttle.RecordFailure("user", options);
        throttle.TrackedKeyCount.Should().Be(1);

        throttle.Clear("user");
        throttle.TrackedKeyCount.Should().Be(0);
    }
}
