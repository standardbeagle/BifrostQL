using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Auth;

/// <summary>
/// Pins the grant-merge half of the per-request <see cref="IGrantResolver"/> hook
/// (S2): the resolver's grants are UNIONED into the <c>permissions</c> context key
/// after <see cref="IdentityContextMapper"/> runs, and <c>permissions</c> stays an
/// owned key so a wire-supplied value can never add to it.
/// </summary>
public sealed class GrantResolverTests
{
    [Fact]
    public void UnionPermissions_OnContextWithoutPermissions_WritesGrants()
    {
        var context = new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultUserIdContextKey] = "user-1",
        };

        IdentityContextMapper.UnionPermissions(context, new[] { "x", "y" });

        PolicyIdentity.ExtractPermissions(context).Should().BeEquivalentTo("x", "y");
    }

    [Fact]
    public void UnionPermissions_UnionsWithExistingPermissions_DedupingCaseInsensitively()
    {
        var context = new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultPermissionsContextKey] = new[] { "existing", "X" },
        };

        IdentityContextMapper.UnionPermissions(context, new[] { "x", "new" });

        PolicyIdentity.ExtractPermissions(context)
            .Should().BeEquivalentTo("existing", "X", "new");
    }

    [Fact]
    public void UnionPermissions_SkipsNullAndWhitespaceGrants()
    {
        var context = new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultPermissionsContextKey] = new[] { "existing" },
        };

        IdentityContextMapper.UnionPermissions(context, new string?[] { null, "", "   " }.Cast<string>());

        PolicyIdentity.ExtractPermissions(context).Should().BeEquivalentTo("existing");
    }

    [Fact]
    public void UnionPermissions_LeavesOtherContextKeysUntouched()
    {
        var context = new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultRolesContextKey] = new[] { "member" },
            [MetadataKeys.Auth.DefaultUserIdContextKey] = "user-1",
        };

        IdentityContextMapper.UnionPermissions(context, new[] { "x" });

        context[MetadataKeys.Auth.DefaultRolesContextKey].Should().BeEquivalentTo(new[] { "member" });
        context[MetadataKeys.Auth.DefaultUserIdContextKey].Should().Be("user-1");
    }

    [Fact]
    public void Permissions_RemainsAnOwnedKey_SoWireContextCannotAddToIt()
    {
        // The owned-key set is what WireContextMerger excludes from a client-supplied
        // context; if `permissions` ever dropped out of it, a wire value could smuggle
        // grants past the resolver.
        new IdentityContextMapper().OwnedKeyNames
            .Should().Contain(MetadataKeys.Auth.DefaultPermissionsContextKey);
    }
}
