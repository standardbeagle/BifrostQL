using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Workflows;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Auth;

/// <summary>
/// The workflow-trigger suppression flag (<c>_bifrostSuppressWorkflowTriggers</c>) is an
/// engine-only privilege: only the workflow runner may suppress post-commit observers.
/// A caller-settable value under that key (provider claim or wire context entry) must
/// never trip the gate — the gate must recognise the engine's own unforgeable marker,
/// not any bool/string a claims provider or wire payload can emit.
/// </summary>
public sealed class WorkflowTriggerSuppressionFlagTests
{
    private static IDbModel EmptyModel() => new DbModel
    {
        Tables = [],
        Metadata = new Dictionary<string, object?>(),
    };

    [Fact]
    public void ProviderClaim_UnderSuppressionKey_DoesNotSuppress()
    {
        var mapper = new IdentityContextMapper();
        var identity = new AppIdentity(
            id: "attacker",
            provider: "oidc:test",
            claims: new Dictionary<string, object?>
            {
                [WorkflowTriggerHost.SuppressTriggersKey] = true,
            });

        var context = mapper.ToUserContext(identity);

        MutationNotifier.IsWorkflowTriggerSuppressed(context).Should().BeFalse(
            "a typed provider claim under the suppression key is external input, not the engine marker");
    }

    [Fact]
    public void ProviderClaim_StringTrue_UnderSuppressionKey_DoesNotSuppress()
    {
        var mapper = new IdentityContextMapper();
        var identity = new AppIdentity(
            id: "attacker",
            provider: "oidc:test",
            claims: new Dictionary<string, object?>
            {
                [WorkflowTriggerHost.SuppressTriggersKey] = "true",
            });

        var context = mapper.ToUserContext(identity);

        MutationNotifier.IsWorkflowTriggerSuppressed(context).Should().BeFalse(
            "a string claim under the suppression key is external input, not the engine marker");
    }

    [Fact]
    public void ProviderClaim_UnderSuppressionKey_IsStrippedFromMappedContext()
    {
        var mapper = new IdentityContextMapper();
        var identity = new AppIdentity(
            id: "attacker",
            provider: "oidc:test",
            claims: new Dictionary<string, object?>
            {
                [WorkflowTriggerHost.SuppressTriggersKey] = true,
            });

        var context = mapper.ToUserContext(identity);

        context.Should().NotContainKey(WorkflowTriggerHost.SuppressTriggersKey,
            "the key is engine-internal; provider claims must not carry it into the user context");
    }

    [Fact]
    public void WireEntry_UnderSuppressionKey_IsStrippedByMerger()
    {
        var userContext = new Dictionary<string, object?>();
        var wire = new Dictionary<string, object?>
        {
            [WorkflowTriggerHost.SuppressTriggersKey] = true,
            ["frontend-extra"] = 42,
        };

        WireContextMerger.Merge(userContext, wire, EmptyModel());

        userContext.Should().NotContainKey(WorkflowTriggerHost.SuppressTriggersKey,
            "the key is engine-internal; a wire entry under it must never merge");
        userContext["frontend-extra"].Should().Be(42);
        MutationNotifier.IsWorkflowTriggerSuppressed(userContext).Should().BeFalse();
    }

    [Fact]
    public void BareBool_InUserContext_DoesNotSuppress()
    {
        // Programmatic route: even a context that already carries a bare bool under the
        // key (e.g. hand-built by a host) is not the engine marker.
        var context = new Dictionary<string, object?>
        {
            [WorkflowTriggerHost.SuppressTriggersKey] = true,
        };

        MutationNotifier.IsWorkflowTriggerSuppressed(context).Should().BeFalse(
            "suppression must require the engine's reference-equal marker, not any bool");
    }
}
