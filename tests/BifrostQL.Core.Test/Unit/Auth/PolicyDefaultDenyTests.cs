using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Auth;

public class PolicyDefaultDenyTests
{
    [Fact]
    public void Deny_default_makes_undeclared_table_unreadable_and_invisible()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("secret", t => t.WithSchema("public").WithPrimaryKey("id"))
            .Build();
        model.Metadata[MetadataKeys.Policy.Default] = "deny";

        var policy = PolicyConfigCollector.FromTable(model, model.Tables.Single());

        policy.HasPolicy.Should().BeTrue();
        new PolicyEvaluator().CanAct(policy, PolicyAction.Read, new AppIdentity("u", "test"))
            .Allowed.Should().BeFalse();
        SchemaReadVisibility.Project(model, new Dictionary<string, object?>())
            .Should().BeEmpty();
    }

    [Fact]
    public void Admin_still_bypasses_deny_default()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("secret", t => t.WithSchema("public").WithPrimaryKey("id"))
            .Build();
        model.Metadata[MetadataKeys.Policy.Default] = "deny";

        var policy = PolicyConfigCollector.FromTable(model, model.Tables.Single());

        new PolicyEvaluator().CanAct(policy, PolicyAction.Read,
            new AppIdentity("u", "test", roles: new[] { MetadataKeys.Policy.DefaultAdminRole })).Allowed.Should().BeTrue();
    }
}
