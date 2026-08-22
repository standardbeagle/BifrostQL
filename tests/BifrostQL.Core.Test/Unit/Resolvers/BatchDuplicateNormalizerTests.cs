using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;
using BatchAction = BifrostQL.Core.Resolvers.BatchMutationPipeline.BatchAction;

namespace BifrostQL.Core.Test.Unit.Resolvers;

/// <summary>
/// The duplicate-handling contract for batches: duplicates are ALWAYS resolved
/// deterministically in Core, before any SQL, on both the per-row and set-based paths.
/// batch-duplicate-policy picks the resolution: 'last-wins' (default) collapses the
/// actions to their sequential net effect; 'reject' refuses the batch cleanly.
/// </summary>
public sealed class BatchDuplicateNormalizerTests
{
    private static IDbTable BuildTable(string? policy = null)
    {
        var fixture = DbModelTestFixture.Create()
            .WithTable("Orders", t =>
            {
                t.WithSchema("dbo").WithPrimaryKey("OrderId").WithPrimaryKey("LineNo")
                    .WithColumn("Status", "nvarchar")
                    .WithColumn("Total", "decimal");
                if (policy is not null)
                    t.WithMetadata("batch-duplicate-policy", policy);
            })
            .Build();
        return fixture.GetTableFromDbName("Orders");
    }

    private static BatchAction Action(MutationAction action, params (string Col, object? Value)[] values)
        => new(action, values.ToDictionary(v => v.Col, v => v.Value));

    [Fact]
    public void DistinctKeys_PassThroughUnchanged()
    {
        var actions = new[]
        {
            Action(MutationAction.Insert, ("Status", "new")),
            Action(MutationAction.Update, ("OrderId", 1), ("LineNo", 1), ("Status", "a")),
            Action(MutationAction.Delete, ("OrderId", 2), ("LineNo", 1)),
        };

        var normalized = BatchDuplicateNormalizer.Normalize(BuildTable(), actions);

        normalized.Should().Equal(actions);
    }

    [Fact]
    public void UpdateThenUpdate_MergesPerColumn_LaterWins()
    {
        // The sequential net effect of two updates is a per-column merge: the second
        // action's columns overwrite, the first's untouched columns survive.
        var normalized = BatchDuplicateNormalizer.Normalize(BuildTable(), new[]
        {
            Action(MutationAction.Update, ("OrderId", 1), ("LineNo", 1), ("Status", "first"), ("Total", 10m)),
            Action(MutationAction.Update, ("OrderId", 1), ("LineNo", 1), ("Status", "second")),
        });

        var survivor = normalized.Should().ContainSingle().Which;
        survivor.Action.Should().Be(MutationAction.Update);
        survivor.Data["Status"].Should().Be("second");
        survivor.Data["Total"].Should().Be(10m, "a column only the earlier update set survives the merge");
    }

    [Fact]
    public void UpdateThenDelete_DeleteAbsorbsTheUpdate()
    {
        var normalized = BatchDuplicateNormalizer.Normalize(BuildTable(), new[]
        {
            Action(MutationAction.Update, ("OrderId", 1), ("LineNo", 1), ("Status", "moot")),
            Action(MutationAction.Delete, ("OrderId", 1), ("LineNo", 1)),
        });

        normalized.Should().ContainSingle().Which.Action.Should().Be(MutationAction.Delete);
    }

    [Fact]
    public void DeleteThenUpdate_DeleteWins_UpdateIsMoot()
    {
        // Sequentially the delete removes the row, then the update matches nothing:
        // the net effect is the delete alone.
        var normalized = BatchDuplicateNormalizer.Normalize(BuildTable(), new[]
        {
            Action(MutationAction.Delete, ("OrderId", 1), ("LineNo", 1)),
            Action(MutationAction.Update, ("OrderId", 1), ("LineNo", 1), ("Status", "ghost")),
        });

        normalized.Should().ContainSingle().Which.Action.Should().Be(MutationAction.Delete);
    }

    [Fact]
    public void DeleteThenDelete_CollapsesToOne()
    {
        var normalized = BatchDuplicateNormalizer.Normalize(BuildTable(), new[]
        {
            Action(MutationAction.Delete, ("OrderId", 1), ("LineNo", 1)),
            Action(MutationAction.Delete, ("OrderId", 1), ("LineNo", 1)),
        });

        normalized.Should().ContainSingle().Which.Action.Should().Be(MutationAction.Delete);
    }

    [Fact]
    public void SurvivorOrder_FollowsLastOccurrence()
    {
        // The merged update takes the LAST occurrence's position: its "final say" happens
        // after the interleaved action on the other key, and order is deterministic.
        var normalized = BatchDuplicateNormalizer.Normalize(BuildTable(), new[]
        {
            Action(MutationAction.Update, ("OrderId", 1), ("LineNo", 1), ("Status", "a")),
            Action(MutationAction.Delete, ("OrderId", 2), ("LineNo", 1)),
            Action(MutationAction.Update, ("OrderId", 1), ("LineNo", 1), ("Status", "b")),
        });

        normalized.Should().HaveCount(2);
        normalized[0].Action.Should().Be(MutationAction.Delete);
        normalized[1].Action.Should().Be(MutationAction.Update);
        normalized[1].Data["Status"].Should().Be("b");
    }

    [Fact]
    public void RejectPolicy_DuplicateKey_IsCleanError_BeforeAnySql()
    {
        var act = () => BatchDuplicateNormalizer.Normalize(BuildTable("reject"), new[]
        {
            Action(MutationAction.Update, ("OrderId", 1), ("LineNo", 1), ("Status", "a")),
            Action(MutationAction.Update, ("OrderId", 1), ("LineNo", 1), ("Status", "b")),
        });

        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*multiple actions*same key*")
            .WithMessage("*batch-duplicate-policy*");
    }

    [Fact]
    public void UpsertCollidingWithAnotherAction_IsCleanError_UnderBothPolicies()
    {
        // An upsert's net effect depends on row existence, so a collision involving one
        // cannot be collapsed deterministically — it is refused, never guessed.
        foreach (var policy in new string?[] { null, "reject" })
        {
            var act = () => BatchDuplicateNormalizer.Normalize(BuildTable(policy), new[]
            {
                Action(MutationAction.Upsert, ("OrderId", 1), ("LineNo", 1), ("Status", "a")),
                Action(MutationAction.Update, ("OrderId", 1), ("LineNo", 1), ("Status", "b")),
            });
            act.Should().Throw<BifrostExecutionError>().WithMessage("*upsert*");
        }
    }

    [Fact]
    public void NonColliding_Upserts_PassThrough()
    {
        var actions = new[]
        {
            Action(MutationAction.Upsert, ("OrderId", 1), ("LineNo", 1), ("Status", "a")),
            Action(MutationAction.Upsert, ("OrderId", 1), ("LineNo", 2), ("Status", "b")),
        };

        BatchDuplicateNormalizer.Normalize(BuildTable(), actions).Should().Equal(actions);
    }

    [Fact]
    public void PredicateDelete_DoesNotCollideWithPkUpdate()
    {
        // A delete keyed by a non-PK predicate has no static row identity: it only
        // collapses against an IDENTICAL predicate, never against PK-keyed actions.
        var normalized = BatchDuplicateNormalizer.Normalize(BuildTable(), new[]
        {
            Action(MutationAction.Delete, ("Status", "old")),
            Action(MutationAction.Update, ("OrderId", 1), ("LineNo", 1), ("Status", "old")),
            Action(MutationAction.Delete, ("Status", "old")),
        });

        normalized.Should().HaveCount(2);
        normalized.Count(a => a.Action == MutationAction.Delete).Should().Be(1, "identical predicate deletes collapse");
        normalized.Count(a => a.Action == MutationAction.Update).Should().Be(1);
    }

    [Fact]
    public void UnknownPolicyValue_FailsFast()
    {
        var act = () => BatchDuplicateNormalizer.Normalize(BuildTable("chaos"), new[]
        {
            Action(MutationAction.Update, ("OrderId", 1), ("LineNo", 1), ("Status", "a")),
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*batch-duplicate-policy*chaos*");
    }
}
