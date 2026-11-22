using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Auth;

public sealed class PolicyIdentityGrantsTests
{
    [Fact]
    public void PermissionsProjectToGrants_AndReadDenyRolesMatch()
    {
        var context = new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultUserIdContextKey] = "user-1",
            [MetadataKeys.Auth.DefaultPermissionsContextKey] = new[] { "rates.view_cost" },
        };
        var identity = PolicyIdentity.FromUserContext(context);
        var policy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Read },
            readDenyColumns: new[] { "cost" },
            readDenyRoles: new[] { "rates.view_cost" });

        identity.Grants.Should().Contain("rates.view_cost");
        new PolicyEvaluator().IsColumnAllowed(policy, "cost", PolicyDirection.Read, identity)
            .Allowed.Should().BeFalse();
    }

    [Fact]
    public void TenantIsProjectedOnlyFromTheKeyTheCallerNames()
    {
        // The tenant lives under the model's tenant-context-key, which only the keyed overload
        // knows. The key-less overload used to read tenant_id whatever the model configured.
        var context = new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultUserIdContextKey] = "user-1",
            [MetadataKeys.Auth.DefaultTenantContextKey] = "mapped-tenant",
            ["org_id"] = "configured-tenant",
        };

        PolicyIdentity.FromUserContext(context).TenantId.Should().BeNull();
        PolicyIdentity.FromUserContext(context, "org_id").TenantId.Should().Be("configured-tenant");
        PolicyIdentity.FromUserContext(context, tenantContextKey: null).TenantId.Should().Be("mapped-tenant");
    }

    [Fact]
    public void PermissionsProjectToRowScopeAndAdminGrant()
    {
        var member = PolicyIdentity.FromUserContext(new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultPermissionsContextKey] = new[] { "member" },
        });
        var admin = PolicyIdentity.FromUserContext(new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultPermissionsContextKey] = new[] { "admin" },
        });
        var policy = new TablePolicy(rowScopeExpression: "tenant_id = {tenant_id}", rowScopeRoles: new[] { "member" })
        {
            AdminRole = "admin",
        };

        member.Grants.Should().Contain("member");
        member.Grants.Should().NotContain("admin");
        admin.Grants.Should().Contain("admin");
        new PolicyEvaluator().CanAct(policy, PolicyAction.Read, admin).Allowed.Should().BeTrue();
    }
}
