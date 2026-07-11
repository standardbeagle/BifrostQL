using System;
using System.Collections.Generic;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Covers <see cref="ModulePriorityFloorGuard"/>: the reserved security band (priority
/// 0-99) is kept for the host's built-in transformers, so a consumer transformer that
/// declares a priority below <see cref="BifrostProfile.SecurityBandFloor"/> is rejected at
/// composition time unless it opts in via <see cref="IAllowSecurityBandPriority"/>. Built-in
/// transformers (BifrostQL.Core assembly) are always exempt.
/// </summary>
public class ModulePriorityFloorGuardTests
{
    /// <summary>A consumer mutation transformer (defined in the test assembly) at a chosen priority.</summary>
    private class ConsumerMutationTransformer : IMutationTransformer
    {
        public ConsumerMutationTransformer(int priority) => Priority = priority;
        public int Priority { get; }
        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context) => false;
        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
            => ValueTask.FromResult(new MutationTransformResult { MutationType = mutationType, Data = data });
    }

    /// <summary>A consumer transformer in the security band that has explicitly opted in.</summary>
    private sealed class OptedInSecurityBandTransformer : ConsumerMutationTransformer, IAllowSecurityBandPriority
    {
        public OptedInSecurityBandTransformer(int priority) : base(priority) { }
    }

    private static void Guard(IEnumerable<IMutationTransformer> transformers)
        => ModulePriorityFloorGuard.EnsureConsumerPrioritiesRespectSecurityFloor(
            transformers, t => t.Priority, "mutation");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(99)]
    public void ConsumerTransformerBelowFloor_WithoutOptIn_Throws(int priority)
    {
        var act = () => Guard(new IMutationTransformer[] { new ConsumerMutationTransformer(priority) });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*reserved security band*")
            .Which.Message.Should().Contain(typeof(ConsumerMutationTransformer).FullName);
    }

    [Theory]
    [InlineData(100)]  // at the floor
    [InlineData(150)]
    [InlineData(200)]
    public void ConsumerTransformerAtOrAboveFloor_Passes(int priority)
    {
        var act = () => Guard(new IMutationTransformer[] { new ConsumerMutationTransformer(priority) });

        act.Should().NotThrow();
    }

    [Fact]
    public void ConsumerTransformerBelowFloor_WithExplicitOptIn_Passes()
    {
        var act = () => Guard(new IMutationTransformer[] { new OptedInSecurityBandTransformer(0) });

        act.Should().NotThrow();
    }

    [Fact]
    public void BuiltInTransformerBelowFloor_IsExempt()
    {
        // TenantMutationTransformer (priority 0) and PolicyMutationTransformer (priority 1)
        // are shipped in the BifrostQL.Core assembly, so the guard must never reject them.
        var act = () => Guard(new IMutationTransformer[]
        {
            new TenantMutationTransformer(),
            new PolicyMutationTransformer(),
        });

        act.Should().NotThrow();
    }

    [Fact]
    public void AggregatesEveryViolationIntoOneException()
    {
        var act = () => Guard(new IMutationTransformer[]
        {
            new ConsumerMutationTransformer(5),
            new ConsumerMutationTransformer(200),   // fine
            new ConsumerMutationTransformer(30),
        });

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("priority 5").And.Contain("priority 30");
    }

    [Fact]
    public void FloorConstant_MatchesTheSecurityBandTop()
    {
        // Locks the floor at the top of the 0-99 security band.
        BifrostProfile.SecurityBandFloor.Should().Be(100);
    }
}
