using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using Xunit;

namespace BifrostQL.Core.Test.Auth;

public class IdentityContextMapperTests
{
    private static AppIdentity SampleIdentity() => new(
        id: "user-42",
        provider: "local",
        email: "user@example.com",
        displayName: "Test User",
        tenantId: "tenant-7",
        orgIds: new[] { "org-1", "org-2" },
        roles: new[] { "admin", "viewer" },
        claims: new Dictionary<string, object?> { ["department"] = "engineering" });

    [Fact]
    public void ToUserContext_DefaultKeys_WritesTenantRolesAndAuditUser()
    {
        var mapper = new IdentityContextMapper();

        var context = mapper.ToUserContext(SampleIdentity());

        // Defaults match what tenant/auto-filter/audit modules read.
        Assert.Equal("tenant-7", context["tenant_id"]);
        Assert.Equal(new[] { "admin", "viewer" }, context["roles"]);
        Assert.Equal("user-42", context["id"]);
    }

    [Fact]
    public void ToUserContext_ConfigurableKeys_WritesUnderOverriddenKeys()
    {
        var mapper = new IdentityContextMapper(
            tenantContextKey: "org_id",
            rolesContextKey: "user_roles",
            userAuditKey: "user_id");

        var context = mapper.ToUserContext(SampleIdentity());

        Assert.Equal("tenant-7", context["org_id"]);
        Assert.Equal(new[] { "admin", "viewer" }, context["user_roles"]);
        Assert.Equal("user-42", context["user_id"]);
        Assert.False(context.ContainsKey("tenant_id"));
        Assert.False(context.ContainsKey("roles"));
        Assert.False(context.ContainsKey("id"));
    }

    [Fact]
    public void ToUserContext_CopiesProviderClaims()
    {
        var mapper = new IdentityContextMapper();

        var context = mapper.ToUserContext(SampleIdentity());

        Assert.Equal("engineering", context["department"]);
    }

    [Fact]
    public void ToUserContext_MappedKeysWinOverSameNamedProviderClaim()
    {
        var identity = new AppIdentity(
            id: "real-id",
            provider: "oidc",
            claims: new Dictionary<string, object?> { ["id"] = "spoofed-id" });
        var mapper = new IdentityContextMapper();

        var context = mapper.ToUserContext(identity);

        Assert.Equal("real-id", context["id"]);
    }

    [Fact]
    public void ToUserContext_NullTenantId_OmitsTenantKey()
    {
        var identity = new AppIdentity(id: "user-1", provider: "local");
        var mapper = new IdentityContextMapper();

        var context = mapper.ToUserContext(identity);

        Assert.False(context.ContainsKey("tenant_id"));
    }

    [Fact]
    public void ToUserContext_NoRoles_WritesEmptyRolesCollection()
    {
        var identity = new AppIdentity(id: "user-1", provider: "local");
        var mapper = new IdentityContextMapper();

        var context = mapper.ToUserContext(identity);

        var roles = Assert.IsAssignableFrom<IReadOnlyList<string>>(context["roles"]);
        Assert.Empty(roles);
    }

    [Fact]
    public void ToUserContext_NullIdentity_Throws()
    {
        var mapper = new IdentityContextMapper();

        Assert.Throws<ArgumentNullException>(() => mapper.ToUserContext(null!));
    }

    [Fact]
    public void Constructor_WhitespaceKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => new IdentityContextMapper(tenantContextKey: "  "));
    }

    [Fact]
    public void Constructor_DefaultKeysMatchMetadataKeyConstants()
    {
        // Guards against the defaults drifting away from the module contracts.
        Assert.Equal("tenant_id", MetadataKeys.Auth.DefaultTenantContextKey);
        Assert.Equal("roles", MetadataKeys.Auth.DefaultRolesContextKey);
        Assert.Equal("id", MetadataKeys.Auth.DefaultUserAuditKey);
    }
}

public class AppIdentityTests
{
    [Fact]
    public void Constructor_NormalizesNullCollectionsToEmpty()
    {
        var identity = new AppIdentity(id: "u", provider: "local");

        Assert.Empty(identity.OrgIds);
        Assert.Empty(identity.Roles);
        Assert.Empty(identity.Claims);
    }

    [Fact]
    public void Constructor_MissingId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new AppIdentity(id: " ", provider: "local"));
    }

    [Fact]
    public void Constructor_MissingProvider_Throws()
    {
        Assert.Throws<ArgumentException>(() => new AppIdentity(id: "u", provider: ""));
    }
}
