using System;
using System.Threading;
using BifrostQL.Server;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BifrostQL.Server.Test.Resp;

/// <summary>
/// The RESP pre-auth read deadline must be ONE cumulative budget from connection start,
/// not a fresh AuthenticationTimeout per read. A per-read reset let an unauthenticated
/// peer hold an admission slot forever by sending any cheap frame just before each read.
///
/// <para>The decision under test moved from <c>RespConnectionHandler.ComputeReadDeadline</c> to
/// <see cref="ProtocolPreAuthDeadline.ReadBudget"/> when the three per-adapter deadlines were
/// folded into the shared session host. Same numbers, same shape, one home: RESP passes
/// <c>clampToIdleWhileArmed: false</c>, so an unauthenticated read is bounded by the pre-auth
/// budget alone and an infinite pre-auth timeout means no deadline rather than an idle one.</para>
/// </summary>
public class RespReadDeadlineTests
{
    private static readonly TimeSpan PreAuth = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    private static (ProtocolPreAuthDeadline Deadline, FakeTimeProvider Clock) Armed(TimeSpan timeout)
    {
        var clock = new FakeTimeProvider();
        return (new ProtocolPreAuthDeadline(timeout, clock, CancellationToken.None), clock);
    }

    [Fact]
    public void Unauthenticated_BudgetShrinksAcrossReads_AndIsNeverReset()
    {
        var (deadline, clock) = Armed(PreAuth);
        using var _ = deadline;

        // A chatty peer sends a frame every 10s. Each successive read sees LESS budget — the
        // deadline is not reset to 30s — and eventually goes non-positive (slot dropped).
        deadline.ReadBudget(false, Idle, clampToIdleWhileArmed: false)
            .Should().Be(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(10));
        deadline.ReadBudget(false, Idle, clampToIdleWhileArmed: false)
            .Should().Be(TimeSpan.FromSeconds(20));
        clock.Advance(TimeSpan.FromSeconds(10));
        deadline.ReadBudget(false, Idle, clampToIdleWhileArmed: false)
            .Should().Be(TimeSpan.FromSeconds(10));
        // Past the cumulative deadline: non-positive ⇒ the caller drops the connection.
        clock.Advance(TimeSpan.FromSeconds(15));
        deadline.ReadBudget(false, Idle, clampToIdleWhileArmed: false)
            .Should().BeLessThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void Authenticated_GetsAFreshIdleTimeoutEachRead()
    {
        var (deadline, clock) = Armed(PreAuth);
        using var _ = deadline;

        // An authenticated pooled client legitimately idles: each read resets to the full idle
        // timeout, regardless of how long the connection has been open.
        deadline.ReadBudget(true, Idle, clampToIdleWhileArmed: false).Should().Be(Idle);
        clock.Advance(TimeSpan.FromHours(5));
        deadline.ReadBudget(true, Idle, clampToIdleWhileArmed: false).Should().Be(Idle);
    }

    [Fact]
    public void InfiniteTimeouts_MapToNoDeadline()
    {
        // Infinite pre-auth timeout ⇒ null budget (no timer) while unauthenticated.
        var (never, _) = Armed(Timeout.InfiniteTimeSpan);
        using var a = never;
        never.IsArmed.Should().BeFalse();
        never.ReadBudget(false, Idle, clampToIdleWhileArmed: false).Should().BeNull();

        // Infinite idle timeout ⇒ null budget while authenticated.
        var (armed, _2) = Armed(PreAuth);
        using var b = armed;
        armed.ReadBudget(true, Timeout.InfiniteTimeSpan, clampToIdleWhileArmed: false).Should().BeNull();
    }

    [Fact]
    public void ACredentialedAction_RetiresTheDeadline_AndOnlyTheReturnToAnonymousReArmsIt()
    {
        var (deadline, clock) = Armed(PreAuth);
        using var _ = deadline;

        deadline.IsArmed.Should().BeTrue("the host arms the deadline at accept");

        // A free action must never move an armed deadline: re-arming is a no-op while armed, so
        // a peer that could reach it gains nothing. This is the renewable-window fail-open
        // (invariant 15) expressed as a method contract.
        clock.Advance(TimeSpan.FromSeconds(10));
        deadline.ReArmOnReturnToAnonymous();
        deadline.ReadBudget(false, Idle, clampToIdleWhileArmed: false)
            .Should().Be(TimeSpan.FromSeconds(20), "an armed deadline is never slid forward");

        deadline.RetireOnCredentialedAction();
        deadline.IsArmed.Should().BeFalse();
        deadline.ReadBudget(false, Idle, clampToIdleWhileArmed: false)
            .Should().BeNull("a retired deadline no longer bounds an unauthenticated read");

        // The transition BACK to anonymous is the one legal re-arm: that state is bounded rather
        // than left unbounded because the session was once authenticated.
        deadline.ReArmOnReturnToAnonymous();
        deadline.IsArmed.Should().BeTrue();
        deadline.ReadBudget(false, Idle, clampToIdleWhileArmed: false).Should().Be(PreAuth);
    }

    [Fact]
    public void ClampToIdleWhileArmed_BoundsAnUnauthenticatedReadByTheShorterOfTheTwo()
    {
        // LDAP's shape: a silent peer is dropped by the idle timeout even under a configuration
        // whose pre-auth window outlives it.
        var (deadline, _) = Armed(TimeSpan.FromMinutes(30));
        using var __ = deadline;

        deadline.ReadBudget(false, Idle, clampToIdleWhileArmed: true).Should().Be(Idle);
        deadline.ReadBudget(false, Idle, clampToIdleWhileArmed: false).Should().Be(TimeSpan.FromMinutes(30));
    }
}
