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

    // ---- security-resolved context keys (row-scope placeholders, auto-filter claims) ----

    private static IDbModel ModelWithTable(params (string Key, string Value)[] metadata)
        => new DbModel
        {
            Tables = new[]
            {
                new DbTable
                {
                    DbName = "orders",
                    GraphQlName = "orders",
                    NormalizedName = "orders",
                    TableSchema = "dbo",
                    TableType = "BASE TABLE",
                    ColumnLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase),
                    GraphQlLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase),
                    Metadata = metadata.ToDictionary(m => m.Key, m => (object?)m.Value),
                },
            },
            Metadata = new Dictionary<string, object?>(),
        };

    [Fact]
    public void RowScopePlaceholder_IsNotFillableFromTheWire()
    {
        // policy-row-scope resolves {household_id} against the user context at request time.
        // A caller whose identity omits the claim must get fail-closed access denial — not a
        // row-scope predicate compiled against its own wire-supplied scope value.
        var model = ModelWithTable((MetadataKeys.Policy.RowScope, "household_id = {household_id}"));
        var userContext = new Dictionary<string, object?>();
        var wire = new Dictionary<string, object?>
        {
            ["household_id"] = "attacker-household",
            ["frontend-extra"] = 1,
        };

        WireContextMerger.Merge(userContext, wire, model);

        userContext.Should().NotContainKey("household_id",
            "a row-scope placeholder is identity, not request data — the wire can never fill it");
        userContext["frontend-extra"].Should().Be(1);
    }

    [Fact]
    public void AutoFilterClaimKey_IsNotFillableFromTheWire()
    {
        // auto-filter scopes every query by userContext["organization_id"]; a wire entry under
        // that key is a scope spoof for a principal whose identity carries no such claim.
        var model = ModelWithTable((MetadataKeys.Security.AutoFilter, "org_id:organization_id"));
        var userContext = new Dictionary<string, object?>();
        var wire = new Dictionary<string, object?>
        {
            ["organization_id"] = "attacker-org",
            ["frontend-extra"] = 1,
        };

        WireContextMerger.Merge(userContext, wire, model);

        userContext.Should().NotContainKey("organization_id",
            "an auto-filter claim key is identity, not request data — the wire can never fill it");
        userContext["frontend-extra"].Should().Be(1);
    }

    [Fact]
    public void IdentityClaimedSecurityKey_StillWinsOverWire_ButIdentityValueSurvives()
    {
        // A claim the identity DOES carry must keep its identity value (owned keys never
        // overwrite), and the security-key strip must not evict identity-populated entries.
        var model = ModelWithTable((MetadataKeys.Policy.RowScope, "household_id = {household_id}"));
        var userContext = new Dictionary<string, object?> { ["household_id"] = "identity-household" };
        var wire = new Dictionary<string, object?> { ["household_id"] = "attacker-household" };

        WireContextMerger.Merge(userContext, wire, model);

        userContext["household_id"].Should().Be("identity-household");
    }
}
