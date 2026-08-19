using System;
using System.Threading;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp;

/// <summary>
/// The RESP pre-auth read deadline must be ONE cumulative budget from connection start,
/// not a fresh AuthenticationTimeout per read. A per-read reset let an unauthenticated
/// peer hold an admission slot forever by sending any cheap frame just before each read.
/// <see cref="RespConnectionHandler.ComputeReadDeadline"/> is the decision under test.
/// </summary>
public class RespReadDeadlineTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    [Fact]
    public void Unauthenticated_BudgetShrinksAcrossReads_AndIsNeverReset()
    {
        var preAuthDeadlineAt = Start + TimeSpan.FromSeconds(30);

        // A chatty peer sends a frame every 10s. Each successive read sees LESS budget — the
        // deadline is not reset to 30s — and eventually goes non-positive (slot dropped).
        RespConnectionHandler.ComputeReadDeadline(false, Start, preAuthDeadlineAt, Idle)
            .Should().Be(TimeSpan.FromSeconds(30));
        RespConnectionHandler.ComputeReadDeadline(false, Start.AddSeconds(10), preAuthDeadlineAt, Idle)
            .Should().Be(TimeSpan.FromSeconds(20));
        RespConnectionHandler.ComputeReadDeadline(false, Start.AddSeconds(20), preAuthDeadlineAt, Idle)
            .Should().Be(TimeSpan.FromSeconds(10));
        // Past the cumulative deadline: non-positive ⇒ the caller drops the connection.
        RespConnectionHandler.ComputeReadDeadline(false, Start.AddSeconds(35), preAuthDeadlineAt, Idle)
            .Should().BeLessThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void Authenticated_GetsAFreshIdleTimeoutEachRead()
    {
        // An authenticated pooled client legitimately idles: each read resets to the full idle
        // timeout, regardless of how long the connection has been open.
        RespConnectionHandler.ComputeReadDeadline(true, Start, Start + TimeSpan.FromSeconds(30), Idle)
            .Should().Be(Idle);
        RespConnectionHandler.ComputeReadDeadline(true, Start.AddHours(5), null, Idle)
            .Should().Be(Idle);
    }

    [Fact]
    public void InfiniteTimeouts_MapToNoDeadline()
    {
        // Infinite pre-auth timeout ⇒ null budget (no CancelAfter) while unauthenticated.
        RespConnectionHandler.ComputeReadDeadline(false, Start, null, Idle).Should().BeNull();
        // Infinite idle timeout ⇒ null budget while authenticated.
        RespConnectionHandler.ComputeReadDeadline(true, Start, null, Timeout.InfiniteTimeSpan).Should().BeNull();
    }
}
