using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Auth;

/// <summary>
/// The single decision point for frontend-parsed WIRE context (BifrostRequest.WireContext):
/// identity keys win, and identity-owned keys — INCLUDING a model-configured
/// tenant-context-key — can never be wire-supplied. The predecessor (the auth factory's
/// merge overload) stripped only the DEFAULT mapper key names, so a deployment configuring
/// tenant-context-key: org_id could still have an unauthenticated caller smuggle its own
/// tenant scope; that is why the merge moved into the engine, after model resolution.
/// </summary>
public sealed class WireContextMergerTests
{
    private static IDbModel Model(string? tenantContextKey = null) => new DbModel
    {
        Tables = [],
        Metadata = tenantContextKey is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?> { [MetadataKeys.Security.TenantContextKey] = tenantContextKey },
    };

    [Fact]
    public void IdentityKeysWin_AndNonIdentityEntriesMerge()
    {
        var userContext = new Dictionary<string, object?> { ["sub"] = "user-1" };
        var wire = new Dictionary<string, object?> { ["sub"] = "spoof", ["frontend-extra"] = 42 };

        WireContextMerger.Merge(userContext, wire, Model());

        userContext["sub"].Should().Be("user-1", "identity-populated keys are never overwritten");
        userContext["frontend-extra"].Should().Be(42);
    }

    [Fact]
    public void OmittedDefaultIdentityKeys_AreNotFillableFromTheWire()
    {
        // The identity carries no tenant/roles, so those slots are ABSENT — a wire entry
        // must not fill them (a tenant-less caller injecting its own tenant scope).
        var userContext = new Dictionary<string, object?>();
        var wire = new Dictionary<string, object?>
        {
            ["tenant_id"] = "attacker-tenant",
            ["roles"] = new[] { "admin" },
            ["user"] = "attacker",
            ["frontend-extra"] = 42,
        };

        WireContextMerger.Merge(userContext, wire, Model());

        userContext.Should().NotContainKey("tenant_id");
        userContext.Should().NotContainKey("roles");
        userContext.Should().NotContainKey("user");
        userContext["frontend-extra"].Should().Be(42);
    }

    [Fact]
    public void ConfiguredCustomTenantContextKey_IsStrippedToo()
    {
        // The gap the transport-time strip could not close: with tenant-context-key
        // configured to org_id, the transformers scope by userContext["org_id"] — a wire
        // entry under that key IS a tenant spoof, even though the default mapper's owned
        // set does not contain it.
        var userContext = new Dictionary<string, object?>();
        var wire = new Dictionary<string, object?> { ["org_id"] = "attacker-org", ["frontend-extra"] = 1 };

        WireContextMerger.Merge(userContext, wire, Model(tenantContextKey: "org_id"));

        userContext.Should().NotContainKey("org_id",
            "the owned-key set must include the model-configured tenant key, not just the defaults");
        userContext["frontend-extra"].Should().Be(1);
    }

    [Fact]
    public void NullOrEmptyWireContext_IsANoOp()
    {
        var userContext = new Dictionary<string, object?> { ["sub"] = "user-1" };

        WireContextMerger.Merge(userContext, null, Model());
        WireContextMerger.Merge(userContext, new Dictionary<string, object?>(), Model());

        userContext.Should().HaveCount(1);
    }
}
