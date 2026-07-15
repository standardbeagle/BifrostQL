using System;
using BifrostQL.Core.Modules.Cdc;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Unit coverage for the dispatcher's backoff, kept a pure function of (attempts, jitter)
/// so it is deterministic under test. Equal-jitter: the delay is uniform in
/// <c>[capped/2, capped]</c> with <c>capped = min(maxDelay, baseDelay·2^(attempts-1))</c> —
/// exponential growth, a hard ceiling, and a floor that guarantees it is never a tight retry.
/// </summary>
public sealed class OutboxBackoffTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

    [Theory]
    // attempts=1 → capped=1s: [0.5s, 1s]
    [InlineData(1, 0.0, 500)]
    [InlineData(1, 1.0, 1000)]
    // attempts=2 → capped=2s: [1s, 2s]
    [InlineData(2, 0.0, 1000)]
    [InlineData(2, 1.0, 2000)]
    // attempts=4 → capped=8s: [4s, 8s]
    [InlineData(4, 0.5, 6000)]
    public void GrowsExponentially_WithEqualJitter(int attempts, double jitter, int expectedMs)
    {
        var delay = OutboxDispatcher.ComputeBackoff(attempts, jitter, Base, Max);

        delay.TotalMilliseconds.Should().BeApproximately(expectedMs, 0.5);
    }

    [Fact]
    public void IsCappedAtMaxDelay()
    {
        // A large attempt count would explode the exponential; it must clamp to maxDelay.
        var delay = OutboxDispatcher.ComputeBackoff(attempts: 40, jitter: 1.0, Base, Max);

        delay.Should().Be(Max);
    }

    [Fact]
    public void NeverReturnsZero_EvenAtMinimumJitter()
    {
        // The equal-jitter floor (capped/2) guarantees a real delay — never a tight retry.
        var delay = OutboxDispatcher.ComputeBackoff(attempts: 1, jitter: 0.0, Base, Max);

        delay.Should().BeGreaterThan(TimeSpan.Zero);
    }
}
