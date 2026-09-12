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
        // Existing strings without brackets parse to EMPTY grant sets —
        // the action is unconditional.
        policy.AllowedActions.Keys.Should().BeEquivalentTo(new[]
        {
            PolicyAction.Read, PolicyAction.Update
        });
        policy.AllowedActions.Values.Should().OnlyContain(g => g.Count == 0);
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
    public void CanAct_AdminRole_DeniedWhenActionNotInAllowList()
    {
        // D7: the admin bypass covers the GRANT requirement only, never an
        // absent action. A policy that lists actions is a product surface — an
        // action it does not list is denied for admins too.
        var policy = new TablePolicy(
            allowedActions: new[] { PolicyAction.Read });
        var evaluator = new PolicyEvaluator();

        var decision = evaluator.CanAct(policy, PolicyAction.Delete, Identity("admin"));

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public void CanAct_AdminRole_AllowedWhenPolicyListsNoActions()
    {
        // D7 carve-out: an empty-actions policy (deny columns only, or
        // policy-default: deny) keeps the historical admin bypass, so
        // PolicyMutationTransformer's admin probe keeps working.
        var policy = new TablePolicy(readDenyColumns: new[] { "ssn" });
        var evaluator = new PolicyEvaluator();

        evaluator.CanAct(policy, PolicyAction.Delete, Identity("admin")).Allowed
            .Should().BeTrue();
    }

    // ---- policy-actions bracket grants (S3) ----

    [Fact]
    public void BracketGrants_CallerWithoutGrant_IsDenied()
    {
        // delete[projects.manage] — a caller holding none of the bracketed
        // grants is refused the action.
        var policy = PolicyConfigCollector.FromTable(TableWithMetadata("projects",
            (MetadataKeys.Policy.Actions, "read,update[projects.manage],delete[projects.manage,invoices.manage]")));
        var evaluator = new PolicyEvaluator();

        evaluator.CanAct(policy, PolicyAction.Delete, Identity("member")).Allowed
            .Should().BeFalse();
        evaluator.CanAct(policy, PolicyAction.Update, Identity("member")).Allowed
            .Should().BeFalse();
    }

    [Fact]
    public void BracketGrants_CallerWithAnyListedGrant_IsAllowed()
    {
        // ANY listed grant passes.
        var policy = PolicyConfigCollector.FromTable(TableWithMetadata("projects",
            (MetadataKeys.Policy.Actions, "read,delete[projects.manage,invoices.manage]")));
        var evaluator = new PolicyEvaluator();

        evaluator.CanAct(policy, PolicyAction.Delete, Identity("member", "invoices.manage")).Allowed
            .Should().BeTrue();
        evaluator.CanAct(policy, PolicyAction.Delete, Identity("member", "projects.manage")).Allowed
            .Should().BeTrue();
    }

    [Fact]
    public void BracketGrants_AdminBypassesGrantRequirement_ButNotAnAbsentAction()
    {
        var policy = PolicyConfigCollector.FromTable(TableWithMetadata("projects",
            (MetadataKeys.Policy.Actions, "read,delete[projects.manage]")));
        var evaluator = new PolicyEvaluator();

        // Admin holds no grants, yet the bracket gate is bypassed (D7 grant half).
        evaluator.CanAct(policy, PolicyAction.Delete, Identity("admin")).Allowed
            .Should().BeTrue();
        // An action the policy does not list is denied even for the admin (D7).
        evaluator.CanAct(policy, PolicyAction.Update, Identity("admin")).Allowed
            .Should().BeFalse();
    }

    [Fact]
    public void BracketGrants_TokenWithoutBrackets_IsUnconditional()
    {
        // No brackets = any caller who passes the rest of the policy.
        var policy = PolicyConfigCollector.FromTable(TableWithMetadata("projects",
            (MetadataKeys.Policy.Actions, "read,delete[projects.manage]")));
        var evaluator = new PolicyEvaluator();

        evaluator.CanAct(policy, PolicyAction.Read, Identity("member")).Allowed
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("read,update[projects.manage")]   // unclosed bracket
    [InlineData("read,update]projects.manage[")]  // stray close before open
    [InlineData("read,update[a]extra")]           // bracket not at the end
    public void Collector_MalformedBracket_FailsLoadWithValidNames(string actions)
    {
        var table = TableWithMetadata("orders", (MetadataKeys.Policy.Actions, actions));

        var act = () => PolicyConfigCollector.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Malformed bracket*");
    }

    [Fact]
    public void Collector_EmptyBracket_FailsLoadWithValidNames()
    {
        // A present-but-empty bracket (`delete[]`) must not silently degrade to
        // UNCONDITIONAL — that is a fail-open typo. It is malformed, full stop.
        var table = TableWithMetadata("orders", (MetadataKeys.Policy.Actions, "read,delete[]"));

        var act = () => PolicyConfigCollector.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Malformed bracket*");
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

        // The custom admin reads freely — the grant requirement is bypassed.
        evaluator.CanAct(policy, PolicyAction.Read, Identity("superuser")).Allowed
            .Should().BeTrue();
        // D7: but an unlisted action is denied to the custom admin too.
        evaluator.CanAct(policy, PolicyAction.Delete, Identity("superuser")).Allowed
            .Should().BeFalse();
        // And the default admin role is not honored when a custom one is configured.
        evaluator.CanAct(policy, PolicyAction.Delete, Identity("admin")).Allowed
            .Should().BeFalse();
    }
}
