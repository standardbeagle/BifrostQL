using System;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The login throttle's failure map only ever removed an entry on a SUCCESSFUL login
/// for that exact key, so a peer rotating login strings or source IPs would grow it
/// without bound on the unauthenticated path. It now evicts entries whose lockout
/// window has fully elapsed once the map is over a soft cap — those carry no live
/// lockout, so dropping them changes no decision while a live lockout is never evicted.
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
    public void OverCap_LiveLockouts_AreNeverEvicted()
    {
        // A large window ⇒ no entry is elapsed, so the prune (which still runs once over the
        // cap) must remove nothing: a live lockout is never dropped to make room.
        var throttle = new LocalAuthEndpoint.LoginThrottle(maxTrackedKeys: 2);
        var options = Options(max: 5, window: TimeSpan.FromHours(1));

        for (var i = 0; i < 5; i++)
            throttle.RecordFailure($"key-{i}", options);

        throttle.TrackedKeyCount.Should().Be(5,
            "pruning must never evict a live (non-elapsed) lockout, even when over the cap");
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
