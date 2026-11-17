using BifrostQL.Server.Auth;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The shared credential-attempt limiter under contention. Every protocol's per-source and
/// per-account cap is this one implementation, so an overshoot here is extra password guesses on
/// every front door.
/// </summary>
public sealed class ProtocolAuthAttemptLimiterConcurrencyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int Racers = 32;
    private const int Trials = 100;

    [Fact]
    public void ConcurrentAttempts_AdmitExactlyTheSourceCap()
    {
        const int cap = 4;
        for (var trial = 0; trial < Trials; trial++)
        {
            var limiter = new LocalLoginRateLimiter(cap, 1000, TimeSpan.FromMinutes(1), () => T0);

            var admitted = Race(i => limiter.TryLogin("10.0.0.1", $"user-{i}"));

            admitted.Should().Be(cap, $"trial {trial}: the source window admits exactly its cap however the attempts interleave");
        }
    }

    [Fact]
    public void ConcurrentAttempts_AdmitExactlyTheAccountCap_AndRefusalsGiveTheSourceSlotBack()
    {
        const int sourceCap = 10;
        const int accountCap = 2;
        for (var trial = 0; trial < Trials; trial++)
        {
            var limiter = new LocalLoginRateLimiter(sourceCap, accountCap, TimeSpan.FromMinutes(1), () => T0);

            var admitted = Race(_ => limiter.TryLogin("10.0.0.1", "alice"));
            admitted.Should().Be(accountCap, $"trial {trial}: the account window admits exactly its cap");

            // The account refusals must not have consumed the source axis: exactly the rest of the
            // source budget is still available to other accounts from the same source.
            var rest = Enumerable.Range(0, sourceCap).Count(i => limiter.TryLogin("10.0.0.1", $"other-{i}"));
            rest.Should().Be(sourceCap - accountCap, $"trial {trial}: only admitted attempts count against the source");
        }
    }

    /// <summary>Runs <see cref="Racers"/> attempts released together by a barrier; returns how many were admitted.</summary>
    private static int Race(Func<int, bool> attempt)
    {
        var admitted = 0;
        using var barrier = new Barrier(Racers);
        var threads = Enumerable.Range(0, Racers).Select(i => new Thread(() =>
        {
            barrier.SignalAndWait();
            if (attempt(i))
                Interlocked.Increment(ref admitted);
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        return admitted;
    }
}
