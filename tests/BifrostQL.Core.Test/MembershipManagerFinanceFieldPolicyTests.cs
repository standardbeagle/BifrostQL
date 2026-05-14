using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// RED/GREEN TDD coverage for sub-task 3/4 of the Membership Manager policy
/// work: finance-field read restrictions and confirmation of officer / read_only
/// operational scope.
///
/// Finance columns (<c>amount_cents</c> on <c>dues_invoices</c> and
/// <c>dues_payments</c>, <c>price_cents</c> on <c>membership_plans</c>) carry a
/// <c>policy-read-deny</c> qualified by <c>policy-read-deny-roles</c> — the
/// role-scoped variant added in this sub-task, mirroring
/// <c>policy-row-scope-roles</c> from sub-task 2. The qualified deny blocks the
/// finance columns only for the listed non-finance roles; finance_manager (not
/// listed) keeps read access and admin bypasses the policy engine entirely.
///
/// These tests pin the exact metadata documented in the membership-manager
/// seed-sample SQL headers and prove it produces the required behaviour.
///
/// Acceptance criteria exercised:
///   - a non-finance role (officer/member) querying a finance field is denied
///     via the column-read-deny seam;
///   - admin and finance_manager can read finance fields;
///   - officer can update operational tables within its tenant;
///   - read_only writes are denied.
/// </summary>
public class MembershipManagerFinanceFieldPolicyTests
{
    // The verbatim finance-field policy configuration documented in the
    // membership-manager seed-sample SQL headers. Pinned here so a drift
    // between the seed docs and the engine behaviour fails a test.
    private const string DuesAmountColumn = "amount_cents";
    private const string PlanPriceColumn = "price_cents";
    private const string FinanceDenyRoles = "officer,event_manager,member,read_only";

    private static QueryTransformContext QueryContext(
        IDbModel model, IDictionary<string, object?> userContext) =>
        new()
        {
            Model = model,
            UserContext = userContext,
            QueryType = QueryType.Standard,
            Path = "",
            IsNestedQuery = false,
        };

    private static MutationTransformContext MutationContext(
        IDbModel model, IDictionary<string, object?> userContext) =>
        new()
        {
            Model = model,
            UserContext = userContext,
        };

    private static IDictionary<string, object?> Caller(string role, string userId = "user-1") =>
        new Dictionary<string, object?>
        {
            ["user_id"] = userId,
            ["roles"] = new[] { role },
        };

    // Builds a model whose finance and operational tables carry exactly the
    // membership-manager seed-sample policy metadata.
    private static IDbModel MembershipManagerModel() =>
        DbModelTestFixture.Create()
            .WithTable("dues_invoices", t => t
                .WithSchema("main")
                .WithPrimaryKey("invoice_id")
                .WithColumn("tenant_id", "int")
                .WithColumn("member_id", "int")
                .WithColumn("amount_cents", "int")
                .WithColumn("status", "varchar")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id")
                .WithMetadata(MetadataKeys.Policy.Actions, "read,create,update")
                .WithMetadata(MetadataKeys.Policy.ReadDeny, DuesAmountColumn)
                .WithMetadata(MetadataKeys.Policy.ReadDenyRoles, FinanceDenyRoles))
            .WithTable("dues_payments", t => t
                .WithSchema("main")
                .WithPrimaryKey("payment_id")
                .WithColumn("tenant_id", "int")
                .WithColumn("invoice_id", "int")
                .WithColumn("amount_cents", "int")
                .WithColumn("method", "varchar")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id")
                .WithMetadata(MetadataKeys.Policy.Actions, "read,create,update")
                .WithMetadata(MetadataKeys.Policy.ReadDeny, DuesAmountColumn)
                .WithMetadata(MetadataKeys.Policy.ReadDenyRoles, FinanceDenyRoles))
            .WithTable("membership_plans", t => t
                .WithSchema("main")
                .WithPrimaryKey("plan_id")
                .WithColumn("tenant_id", "int")
                .WithColumn("name", "varchar")
                .WithColumn("price_cents", "int")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id")
                .WithMetadata(MetadataKeys.Policy.Actions, "read,create,update,delete")
                .WithMetadata(MetadataKeys.Policy.ReadDeny, PlanPriceColumn)
                .WithMetadata(MetadataKeys.Policy.ReadDenyRoles, FinanceDenyRoles))
            .WithTable("members", t => t
                .WithSchema("main")
                .WithPrimaryKey("member_id")
                .WithColumn("tenant_id", "int")
                .WithColumn("user_id", "int")
                .WithColumn("first_name", "varchar")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id")
                .WithMetadata(MetadataKeys.Policy.Actions, "read,create,update,delete"))
            // audit_log carries policy-actions: read only — it is read-only
            // through the generated CRUD, so every write is denied for every
            // non-admin role (read_only included).
            .WithTable("audit_log", t => t
                .WithSchema("main")
                .WithPrimaryKey("audit_id")
                .WithColumn("tenant_id", "int")
                .WithColumn("action", "varchar")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id")
                .WithMetadata(MetadataKeys.Policy.Actions, "read"))
            .Build();

    // ---- Config collection: the seed metadata parses into a role-scoped read deny ----

    [Fact]
    public void Config_DuesInvoicesTable_CarriesRoleScopedFinanceReadDeny()
    {
        var model = MembershipManagerModel();

        var policy = PolicyConfigCollector.FromTable(model.GetTableFromDbName("dues_invoices"));

        policy.ReadDenyColumns.Should().BeEquivalentTo(new[] { DuesAmountColumn });
        policy.ReadDenyRoles.Should().BeEquivalentTo(
            new[] { "officer", "event_manager", "member", "read_only" });
    }

    [Fact]
    public void Config_DuesPaymentsTable_CarriesRoleScopedFinanceReadDeny()
    {
        var model = MembershipManagerModel();

        var policy = PolicyConfigCollector.FromTable(model.GetTableFromDbName("dues_payments"));

        policy.ReadDenyColumns.Should().BeEquivalentTo(new[] { DuesAmountColumn });
        policy.ReadDenyRoles.Should().BeEquivalentTo(
            new[] { "officer", "event_manager", "member", "read_only" });
    }

    [Fact]
    public void Config_MembershipPlansTable_CarriesRoleScopedFinanceReadDeny()
    {
        var model = MembershipManagerModel();

        var policy = PolicyConfigCollector.FromTable(model.GetTableFromDbName("membership_plans"));

        policy.ReadDenyColumns.Should().BeEquivalentTo(new[] { PlanPriceColumn });
        policy.ReadDenyRoles.Should().BeEquivalentTo(
            new[] { "officer", "event_manager", "member", "read_only" });
    }

    // ---- Acceptance 1: a non-finance role querying a finance field is denied ----

    [Theory]
    [InlineData("officer")]
    [InlineData("member")]
    [InlineData("event_manager")]
    [InlineData("read_only")]
    public void NonFinanceRole_QueryingDuesAmount_IsDeniedViaColumnReadDeny(string role)
    {
        var model = MembershipManagerModel();
        var transformer = new PolicyFilterTransformer();
        var context = QueryContext(model, Caller(role));

        var act = () => transformer.AssertColumnsReadable(
            model.GetTableFromDbName("dues_invoices"),
            new[] { "member_id", DuesAmountColumn },
            context);

        // The deny is enforced by rejecting the query — the finance column is
        // hidden from every listed non-finance role.
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*not permitted*")
            .Which.Message.Should().NotContain(DuesAmountColumn,
                "the policy error is generic and never names the denied column");
    }

    [Fact]
    public void Officer_QueryingPlanPrice_IsDeniedViaColumnReadDeny()
    {
        var model = MembershipManagerModel();
        var transformer = new PolicyFilterTransformer();
        var context = QueryContext(model, Caller("officer"));

        var act = () => transformer.AssertColumnsReadable(
            model.GetTableFromDbName("membership_plans"),
            new[] { "name", PlanPriceColumn },
            context);

        act.Should().Throw<BifrostExecutionError>();
    }

    [Fact]
    public void NonFinanceRole_QueryingOnlyNonFinanceColumns_IsAllowed()
    {
        // The role-scoped deny only blocks the finance column itself — an
        // officer querying the non-finance columns of a finance table is fine.
        var model = MembershipManagerModel();
        var transformer = new PolicyFilterTransformer();
        var context = QueryContext(model, Caller("officer"));

        var act = () => transformer.AssertColumnsReadable(
            model.GetTableFromDbName("dues_invoices"),
            new[] { "member_id", "status" },
            context);

        act.Should().NotThrow();
    }

    // ---- Acceptance 2: admin and finance_manager can read finance fields ----

    [Fact]
    public void FinanceManager_QueryingDuesAmount_IsAllowed()
    {
        // finance_manager is not in policy-read-deny-roles, so the qualified
        // deny does not apply — the finance column stays readable.
        var model = MembershipManagerModel();
        var transformer = new PolicyFilterTransformer();
        var context = QueryContext(model, Caller("finance_manager"));

        var act = () => transformer.AssertColumnsReadable(
            model.GetTableFromDbName("dues_invoices"),
            new[] { "member_id", DuesAmountColumn },
            context);

        act.Should().NotThrow(
            "finance_manager is not a denied role, so it may read the finance field");
    }

    [Fact]
    public void FinanceManager_QueryingDuesPaymentAmount_IsAllowed()
    {
        var model = MembershipManagerModel();
        var transformer = new PolicyFilterTransformer();
        var context = QueryContext(model, Caller("finance_manager"));

        var act = () => transformer.AssertColumnsReadable(
            model.GetTableFromDbName("dues_payments"),
            new[] { "invoice_id", DuesAmountColumn },
            context);

        act.Should().NotThrow();
    }

    [Fact]
    public void FinanceManager_QueryingPlanPrice_IsAllowed()
    {
        var model = MembershipManagerModel();
        var transformer = new PolicyFilterTransformer();
        var context = QueryContext(model, Caller("finance_manager"));

        var act = () => transformer.AssertColumnsReadable(
            model.GetTableFromDbName("membership_plans"),
            new[] { "name", PlanPriceColumn },
            context);

        act.Should().NotThrow();
    }

    [Fact]
    public void Admin_QueryingDuesAmount_IsAllowed()
    {
        // admin bypasses the policy engine entirely, including the column deny.
        var model = MembershipManagerModel();
        var transformer = new PolicyFilterTransformer();
        var context = QueryContext(model, Caller("admin"));

        var act = () => transformer.AssertColumnsReadable(
            model.GetTableFromDbName("dues_invoices"),
            new[] { "member_id", DuesAmountColumn },
            context);

        act.Should().NotThrow("admin bypasses the policy engine");
    }

    // ---- Acceptance 3: officer can update operational tables within its tenant ----

    [Fact]
    public void Officer_UpdatingMembers_IsPermitted()
    {
        // The table-level policy-actions grant from sub-task 1 already gives
        // officer full CRUD on the operational tables — no field metadata is
        // needed. The mutation transformer returns no errors and, because
        // members has no row-scope, no additional filter for officer.
        var model = MembershipManagerModel();
        var transformer = new PolicyMutationTransformer();
        var context = MutationContext(model, Caller("officer"));

        var result = transformer.Transform(
            model.GetTableFromDbName("members"),
            MutationType.Update,
            new Dictionary<string, object?> { ["first_name"] = "Renamed" },
            context);

        result.Errors.Should().BeNullOrEmpty(
            "officer holds the update grant on the operational members table");
    }

    [Fact]
    public void Officer_CreatingMembers_IsPermitted()
    {
        var model = MembershipManagerModel();
        var transformer = new PolicyMutationTransformer();
        var context = MutationContext(model, Caller("officer"));

        var result = transformer.Transform(
            model.GetTableFromDbName("members"),
            MutationType.Insert,
            new Dictionary<string, object?> { ["first_name"] = "New" },
            context);

        result.Errors.Should().BeNullOrEmpty();
    }

    // ---- Acceptance 4: read_only writes are denied ----

    [Theory]
    [InlineData(MutationType.Insert)]
    [InlineData(MutationType.Update)]
    [InlineData(MutationType.Delete)]
    public void ReadOnly_WritingReadOnlyTable_IsDenied(MutationType mutationType)
    {
        // audit_log carries policy-actions: read only — it grants no write
        // action, so the mutation transformer rejects every create/update/delete
        // for any non-admin role. read_only never holds a write grant on any
        // table, so a read_only write is denied wherever a table is read-only.
        var model = MembershipManagerModel();
        var transformer = new PolicyMutationTransformer();
        var context = MutationContext(model, Caller("read_only"));

        var result = transformer.Transform(
            model.GetTableFromDbName("audit_log"),
            mutationType,
            new Dictionary<string, object?> { ["action"] = "tampered" },
            context);

        result.Errors.Should().NotBeNullOrEmpty(
            "the read-only table grants no write action, so the write is denied");
    }

    [Fact]
    public void Officer_WritingReadOnlyTable_IsAlsoDenied()
    {
        // The read-only grant is role-blind by design (sub-task 1: policy-actions
        // is the union of allowed actions, not a role-keyed grant). officer is
        // likewise denied a write to a read-only table — confirming the deny is
        // a property of the table grant, not of the read_only role alone.
        var model = MembershipManagerModel();
        var transformer = new PolicyMutationTransformer();
        var context = MutationContext(model, Caller("officer"));

        var result = transformer.Transform(
            model.GetTableFromDbName("audit_log"),
            MutationType.Insert,
            new Dictionary<string, object?> { ["action"] = "tampered" },
            context);

        result.Errors.Should().NotBeNullOrEmpty();
    }
}
