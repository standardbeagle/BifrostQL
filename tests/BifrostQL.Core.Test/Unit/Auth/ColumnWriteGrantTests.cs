using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Auth;

/// <summary>
/// RED/GREEN TDD coverage for S4a write-side column grants: the column-selector
/// <c>write-requires</c> key (collected into <see cref="TablePolicy.WriteRequires"/>),
/// the table-level <c>policy-write-deny-roles</c> qualifier, and their enforcement
/// through <see cref="PolicyEvaluator.IsColumnAllowed"/> and
/// <see cref="PolicyMutationTransformer"/>.
///
/// Write-deny semantics are PRESENCE-keyed (E3/E4): sending the column at all —
/// even with the value already stored — is a write. <c>write-requires</c> never
/// gates a DELETE (E5): a delete carries no writable columns.
/// </summary>
public sealed class ColumnWriteGrantTests
{
    private static MutationTransformContext Context(
        IDbModel model, IDictionary<string, object?>? userContext = null) =>
        new()
        {
            Model = model,
            UserContext = userContext ?? new Dictionary<string, object?>(),
        };

    private static IDictionary<string, object?> UserWithRoles(params string[] roles) =>
        new Dictionary<string, object?>
        {
            ["user_id"] = "user-1",
            ["roles"] = roles,
        };

    private static IDbModel ModelWithCostRateGrant(
        string writeRequires = "team.manage",
        params (string key, string value)[] tableMetadata)
    {
        var builder = DbModelTestFixture.Create()
            .WithTable("users", t =>
            {
                t.WithSchema("public")
                    .WithPrimaryKey("id")
                    .WithColumn("cost_rate", "decimal")
                    .WithColumn("name")
                    .WithMetadata(MetadataKeys.Policy.Actions, "read,create,update,delete")
                    .WithColumnMetadata("cost_rate", MetadataKeys.Policy.WriteRequires, writeRequires);
                foreach (var (key, value) in tableMetadata)
                    t.WithMetadata(key, value);
            });
        return builder.Build();
    }

    private static IDbTable Users(IDbModel model) => model.GetTableFromDbName("users");

    private static AppIdentity Identity(params string[] roles) =>
        new("user-1", "local", roles: roles);

    // ---- PolicyConfigCollector ----

    [Fact]
    public void Collector_ParsesWriteRequires_FromColumnSelectorMetadata()
    {
        var model = ModelWithCostRateGrant();

        var policy = PolicyConfigCollector.FromTable(Users(model));

        policy.HasPolicy.Should().BeTrue();
        policy.WriteRequires.Should().ContainKey("cost_rate");
        policy.WriteRequires["cost_rate"].Should().BeEquivalentTo("team.manage");
    }

    [Fact]
    public void Collector_ParsesWriteDenyRoles_FromTableMetadata()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("public")
                .WithPrimaryKey("id")
                .WithColumn("total", "decimal")
                .WithMetadata(MetadataKeys.Policy.WriteDeny, "total")
                .WithMetadata(MetadataKeys.Policy.WriteDenyRoles, "member"))
            .Build();

        var policy = PolicyConfigCollector.FromTable(model.GetTableFromDbName("orders"));

        policy.WriteDenyColumns.Should().BeEquivalentTo("total");
        policy.WriteDenyRoles.Should().BeEquivalentTo("member");
    }

    // ---- PolicyEvaluator: write-requires ----

    private static TablePolicy RequiresPolicy() =>
        new(
            allowedActions: new[] { PolicyAction.Update },
            writeRequires: new Dictionary<string, IEnumerable<string>>
            {
                ["cost_rate"] = new[] { "team.manage" },
            });

    [Fact]
    public void Evaluator_WriteRequires_DeniesCallerLackingGrant()
    {
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.IsColumnAllowed(
            RequiresPolicy(), "cost_rate", PolicyDirection.Write, Identity("member"));

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public void Evaluator_WriteRequires_AllowsGrantHolder()
    {
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.IsColumnAllowed(
            RequiresPolicy(), "cost_rate", PolicyDirection.Write, Identity("member", "team.manage"));

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Evaluator_WriteRequires_DoesNotRestrictRead()
    {
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.IsColumnAllowed(
            RequiresPolicy(), "cost_rate", PolicyDirection.Read, Identity("member"));

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Evaluator_WriteRequires_AdminBypasses()
    {
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.IsColumnAllowed(
            RequiresPolicy(), "cost_rate", PolicyDirection.Write, Identity("admin"));

        decision.Allowed.Should().BeTrue();
    }

    // ---- PolicyEvaluator: policy-write-deny-roles ----

    [Fact]
    public void Evaluator_WriteDenyRoles_DeniesListedRole_AllowsOtherRoles()
    {
        var evaluator = new PolicyEvaluator();
        var policy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Update },
            writeDenyColumns: new[] { "total" },
            writeDenyRoles: new[] { "member" });

        evaluator.IsColumnAllowed(policy, "total", PolicyDirection.Write, Identity("member"))
            .Allowed.Should().BeFalse("the deny is qualified to the member role");
        evaluator.IsColumnAllowed(policy, "total", PolicyDirection.Write, Identity("accounting"))
            .Allowed.Should().BeTrue("a caller holding none of the deny roles may write");
        evaluator.IsColumnAllowed(policy, "total", PolicyDirection.Write, Identity("admin"))
            .Allowed.Should().BeTrue("admin bypass");
    }

    [Fact]
    public void Evaluator_UnconditionalWriteDeny_StillDeniesEveryNonAdmin()
    {
        var evaluator = new PolicyEvaluator();
        var policy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Update },
            writeDenyColumns: new[] { "total" });

        evaluator.IsColumnAllowed(policy, "total", PolicyDirection.Write, Identity("accounting"))
            .Allowed.Should().BeFalse();
    }

    // ---- PolicyMutationTransformer (E3/E4 presence-keyed, E5 delete-exempt) ----

    [Fact]
    public async Task Transform_UpdateWritingRequiresGatedColumn_AsMember_Denied()
    {
        var model = ModelWithCostRateGrant();
        var transformer = new PolicyMutationTransformer();
        // E4: sending the column at all is a write, whatever the value.
        var data = new Dictionary<string, object?> { ["cost_rate"] = 42m };

        var result = await transformer.TransformAsync(
            Users(model), MutationType.Update, data, Context(model, UserWithRoles("member")));

        result.Errors.Should().ContainSingle();
        result.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
        result.Errors[0].Should().NotContain("cost_rate");
        result.Errors[0].Should().NotContain("users");
    }

    [Fact]
    public async Task Transform_UpdateWithoutGatedColumn_AsMember_Succeeds()
    {
        // E3: the gate keys on the column's PRESENCE in the payload, so an
        // update that does not touch cost_rate is unaffected.
        var model = ModelWithCostRateGrant();
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["name"] = "ada" };

        var result = await transformer.TransformAsync(
            Users(model), MutationType.Update, data, Context(model, UserWithRoles("member")));

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_UpdateWritingGatedColumn_AsGrantHolder_Succeeds()
    {
        var model = ModelWithCostRateGrant();
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["cost_rate"] = 42m };

        var result = await transformer.TransformAsync(
            Users(model), MutationType.Update, data, Context(model, UserWithRoles("member", "team.manage")));

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_DeleteCarryingGatedColumn_NotBlockedByWriteRequires()
    {
        // E5: write-requires never applies to DELETE — a delete carries no
        // writable columns; action brackets (policy-actions) are the delete tool.
        var model = ModelWithCostRateGrant();
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["id"] = 1, ["cost_rate"] = 42m };

        var result = await transformer.TransformAsync(
            Users(model), MutationType.Delete, data, Context(model, UserWithRoles("member")));

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_WriteDenyRoles_MemberDenied_AccountingAllowed()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("public")
                .WithPrimaryKey("id")
                .WithColumn("total", "decimal")
                .WithMetadata(MetadataKeys.Policy.Actions, "update")
                .WithMetadata(MetadataKeys.Policy.WriteDeny, "total")
                .WithMetadata(MetadataKeys.Policy.WriteDenyRoles, "member"))
            .Build();
        var orders = model.GetTableFromDbName("orders");
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["total"] = 10m };

        var denied = await transformer.TransformAsync(
            orders, MutationType.Update, data, Context(model, UserWithRoles("member")));
        denied.Errors.Should().ContainSingle();
        denied.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);

        var allowed = await transformer.TransformAsync(
            orders, MutationType.Update, data, Context(model, UserWithRoles("accounting")));
        allowed.Errors.Should().BeEmpty();

        var admin = await transformer.TransformAsync(
            orders, MutationType.Update, data, Context(model, UserWithRoles("admin")));
        admin.Errors.Should().BeEmpty();
    }
}
