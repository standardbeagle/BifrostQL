using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Auth;

public sealed class WritableValuesTests
{
    [Fact]
    public async Task ValueList_IsPresenceKeyed_Coerced_AndAdminBypassed()
    {
        var model = DbModelTestFixture.Create().WithTable("profiles", t => t
            .WithSchema("public").WithPrimaryKey("id").WithColumn("is_builtin", "bool")
            .WithMetadata(MetadataKeys.Policy.Actions, "create,update")
            .WithColumnMetadata("is_builtin", MetadataKeys.Policy.WritableValues, "false"))
            .Build();
        var table = model.GetTableFromDbName("profiles");
        var transformer = new PolicyMutationTransformer();
        static MutationTransformContext C(IDbModel m, params string[] roles) => new()
        {
            Model = m, UserContext = new Dictionary<string, object?> { ["roles"] = roles }
        };

        (await transformer.TransformAsync(table, MutationType.Insert,
            new() { ["is_builtin"] = "true" }, C(model, "profiles.manage")))
            .ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
        (await transformer.TransformAsync(table, MutationType.Insert,
            new() { ["is_builtin"] = "false" }, C(model, "profiles.manage")))
            .Errors.Should().BeEmpty();
        (await transformer.TransformAsync(table, MutationType.Insert,
            new(), C(model, "profiles.manage"))).Errors.Should().BeEmpty();
        (await transformer.TransformAsync(table, MutationType.Update,
            new() { ["is_builtin"] = true }, C(model, "profiles.manage")))
            .Errors.Should().ContainSingle();
        (await transformer.TransformAsync(table, MutationType.Update,
            new(), C(model, "profiles.manage"))).Errors.Should().BeEmpty();
        (await transformer.TransformAsync(table, MutationType.Insert,
            new() { ["is_builtin"] = true }, C(model, "admin"))).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Collector_ParsesStringValuesCaseInsensitively()
    {
        var model = DbModelTestFixture.Create().WithTable("profiles", t => t
            .WithSchema("public").WithPrimaryKey("id").WithColumn("state", "varchar")
            .WithColumnMetadata("state", MetadataKeys.Policy.WritableValues, "draft,submitted"))
            .Build();
        PolicyConfigCollector.FromTable(model.GetTableFromDbName("profiles"))
            .WritableValues["state"].Should().BeEquivalentTo("draft", "submitted");
    }

    [Fact]
    public async Task StringValues_AreCaseInsensitive_AndDoNotRequireGrant()
    {
        var model = DbModelTestFixture.Create().WithTable("profiles", t => t
            .WithSchema("public").WithPrimaryKey("id").WithColumn("state", "varchar")
            .WithColumnMetadata("state", MetadataKeys.Policy.WritableValues, "draft,submitted")
            .WithColumnMetadata("state", MetadataKeys.Policy.WriteRequires, "profiles.manage"))
            .Build();
        var table = model.GetTableFromDbName("profiles");
        var transformer = new PolicyMutationTransformer();
        static MutationTransformContext C(IDbModel m, params string[] grants) => new()
        {
            Model = m, UserContext = new Dictionary<string, object?> { ["roles"] = grants }
        };

        (await transformer.TransformAsync(table, MutationType.Insert,
            new() { ["state"] = "Draft" }, C(model, "profiles.manage"))).Errors.Should().BeEmpty();
        (await transformer.TransformAsync(table, MutationType.Insert,
            new() { ["state"] = "Draft" }, C(model))).Errors.Should().ContainSingle();
        var refused = await transformer.TransformAsync(table, MutationType.Insert,
            new() { ["state"] = "approved" }, C(model, "profiles.manage"));
        refused.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
        refused.Errors.Should().ContainSingle()
            .Which.Should().Be("The mutation writes a field that is not permitted by authorization policy.");
    }
}
