using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// RED/GREEN TDD coverage for the policy engine foundation: the pure-data
/// <see cref="TablePolicy"/> model, the <see cref="PolicyConfigCollector"/>
/// metadata parser, and the stateless <see cref="PolicyEvaluator"/>.
///
/// Sub-task 1/4 only — row-scope expressions are parsed and stored here but
/// their compilation into a filter is sub-task 2's responsibility.
/// </summary>
public class PolicyEvaluatorTests
{
    private static IDbTable TableWithMetadata(string dbName, params (string key, object? value)[] metadata)
    {
        var table = Substitute.For<IDbTable>();
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in metadata)
            dict[key] = value;
        table.DbName.Returns(dbName);
        table.TableSchema.Returns("dbo");
        table.Metadata.Returns(dict);
        table.GetMetadataValue(Arg.Any<string>())
            .Returns(ci => dict.TryGetValue((string)ci[0], out var v) ? v?.ToString() : null);
        return table;
    }

    private static AppIdentity Identity(params string[] roles) =>
        new("user-1", "local", roles: roles);

    // ---- PolicyConfigCollector: metadata parsing ----

    [Fact]
    public void Collector_ParsesAllowedActions_FromMetadata()
    {
        var table = TableWithMetadata("orders",
            (MetadataKeys.Policy.Actions, "read,update"));

        var policy = PolicyConfigCollector.FromTable(table);

        policy.HasPolicy.Should().BeTrue();
        policy.AllowedActions.Should().BeEquivalentTo(new[]
        {
            PolicyAction.Read, PolicyAction.Update
        });
    }

    [Fact]
    public void Collector_ParsesColumnDenyLists_FromMetadata()
    {
        var table = TableWithMetadata("employees",
            (MetadataKeys.Policy.ReadDeny, "ssn, salary"),
            (MetadataKeys.Policy.WriteDeny, "id"));

        var policy = PolicyConfigCollector.FromTable(table);

        policy.ReadDenyColumns.Should().BeEquivalentTo("ssn", "salary");
        policy.WriteDenyColumns.Should().BeEquivalentTo("id");
    }

    [Fact]
    public void Collector_ParsesRowScopeExpression_VerbatimWithoutCompiling()
    {
        var table = TableWithMetadata("orders",
            (MetadataKeys.Policy.RowScope, "tenant_id = {tenant_id}"));

        var policy = PolicyConfigCollector.FromTable(table);

        // Sub-task 1 only stores the expression; compilation is sub-task 2.
        policy.RowScopeExpression.Should().Be("tenant_id = {tenant_id}");
    }

    [Fact]
    public void Collector_NoPolicyMetadata_ReturnsNoneSentinel()
    {
        var table = TableWithMetadata("public_data");

        var policy = PolicyConfigCollector.FromTable(table);

        policy.HasPolicy.Should().BeFalse();
        policy.Should().BeSameAs(TablePolicy.None);
    }

    [Fact]
    public void Collector_InvalidActionToken_Throws()
    {
        // A bogus action token must fail rather than be silently dropped: silently
        // narrowing the grant hides the operator's mistake, and in the all-invalid
        // case collapses the policy to allow-all (fail-open). See PolicyConfigCollector.
        var table = TableWithMetadata("orders",
            (MetadataKeys.Policy.Actions, "read, bogus, delete"));

        var act = () => PolicyConfigCollector.FromTable(table);

        act.Should().Throw<InvalidOperationException>().WithMessage("*bogus*");
    }

    // ---- PolicyEvaluator: table-action checks ----

    [Fact]
    public void CanAct_ExplicitAllow_Allows()
    {
        var policy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Read, PolicyAction.Update });
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.CanAct(policy, PolicyAction.Read, Identity("viewer"));

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void CanAct_ExplicitDeny_DeniesWithNonLeakingMessage()
    {
        var policy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Read });
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.CanAct(policy, PolicyAction.Delete, Identity("viewer"));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().NotBeNullOrWhiteSpace();
        // Must not leak schema/data details: no table or column names.
        decision.Reason.Should().NotContain("Delete");
    }

    [Fact]
    public void CanAct_AdminRole_AllowsEvenWhenActionNotInAllowList()
    {
        var policy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Read });
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.CanAct(policy, PolicyAction.Delete, Identity("admin"));

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void CanAct_AbsentPolicy_AllowsByDefault()
    {
        // Documented default: policy is opt-in. A table with no policy metadata
        // (TablePolicy.None) imposes no restriction — consistent with how
        // TenantFilter / SoftDelete behave when their metadata is absent.
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.CanAct(TablePolicy.None, PolicyAction.Delete, Identity());

        decision.Allowed.Should().BeTrue();
    }

    // ---- PolicyEvaluator: column read/write checks ----

    [Fact]
    public void IsColumnAllowed_ReadDeniedColumn_Denies()
    {
        var policy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Read },
            readDenyColumns: new[] { "ssn" });
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.IsColumnAllowed(policy, "ssn", PolicyDirection.Read, Identity("viewer"));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().NotContain("ssn");
    }

    [Fact]
    public void IsColumnAllowed_ColumnNotInDenyList_Allows()
    {
        var policy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Read },
            readDenyColumns: new[] { "ssn" });
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.IsColumnAllowed(policy, "name", PolicyDirection.Read, Identity("viewer"));

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void IsColumnAllowed_WriteDeniedColumn_Denies()
    {
        var policy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Update },
            writeDenyColumns: new[] { "id" });
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.IsColumnAllowed(policy, "id", PolicyDirection.Write, Identity("editor"));

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public void IsColumnAllowed_WriteDenyDoesNotAffectRead()
    {
        var policy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Read, PolicyAction.Update },
            writeDenyColumns: new[] { "id" });
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.IsColumnAllowed(policy, "id", PolicyDirection.Read, Identity("viewer"));

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void IsColumnAllowed_AdminRole_AllowsDeniedColumn()
    {
        var policy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Read },
            readDenyColumns: new[] { "ssn" });
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.IsColumnAllowed(policy, "ssn", PolicyDirection.Read, Identity("admin"));

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void IsColumnAllowed_AbsentPolicy_AllowsByDefault()
    {
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.IsColumnAllowed(TablePolicy.None, "ssn", PolicyDirection.Read, Identity());

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void IsColumnAllowed_DenyMatchIsCaseInsensitive()
    {
        var policy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Read },
            readDenyColumns: new[] { "SSN" });
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.IsColumnAllowed(policy, "ssn", PolicyDirection.Read, Identity("viewer"));

        decision.Allowed.Should().BeFalse();
    }

    // ---- PolicyEvaluator: custom admin role ----

    [Fact]
    public void CanAct_CustomAdminRole_IsHonored()
    {
        var policy = new TablePolicy(allowedActions: new[] { PolicyAction.Read });
        var evaluator = new PolicyEvaluator(adminRole: "superuser");

        evaluator.CanAct(policy, PolicyAction.Delete, Identity("superuser")).Allowed
            .Should().BeTrue();
        evaluator.CanAct(policy, PolicyAction.Delete, Identity("admin")).Allowed
            .Should().BeFalse();
    }
}
