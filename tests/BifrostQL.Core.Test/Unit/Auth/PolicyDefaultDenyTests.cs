using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Test.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Auth;

/// <summary>
/// Behaviour of the model-wide deny default, driven through the production
/// loader (<see cref="PolicyDefaultDenyModel"/>) rather than by hand-stamping
/// <c>policy-default</c> onto a fixture table — a hand stamp proves only
/// <c>PolicyConfigCollector</c>'s parsing and stays green even when no real
/// model is ever stamped.
/// </summary>
public class PolicyDefaultDenyTests
{
    [Fact]
    public async Task Deny_default_makes_undeclared_table_unreadable_and_invisible()
    {
        await using var loaded = await PolicyDefaultDenyModel.LoadAsync(
            ":root { policy-default: deny }");

        var policy = loaded.Policy();

        policy.HasPolicy.Should().BeTrue();
        new PolicyEvaluator().CanAct(policy, PolicyAction.Read, new AppIdentity("u", "test"))
            .Allowed.Should().BeFalse();
        SchemaReadVisibility.Project(loaded.Model, new Dictionary<string, object?>())
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Admin_still_bypasses_deny_default()
    {
        await using var loaded = await PolicyDefaultDenyModel.LoadAsync(
            ":root { policy-default: deny }");

        new PolicyEvaluator().CanAct(loaded.Policy(), PolicyAction.Read,
            new AppIdentity("u", "test", roles: new[] { MetadataKeys.Policy.DefaultAdminRole }))
            .Allowed.Should().BeTrue();
    }
}
