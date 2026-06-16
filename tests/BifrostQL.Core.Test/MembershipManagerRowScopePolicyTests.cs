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
/// RED/GREEN TDD coverage for sub-task 2/4 of the Membership Manager policy
/// work: the <c>member</c>-role row-scope configuration on the <c>members</c>
/// and <c>households</c> tables.
///
/// This sub-task is configuration plus tests — the row-scope mechanism itself
/// (<see cref="RowScopeCompiler"/>, <see cref="PolicyFilterTransformer"/>,
/// <see cref="PolicyMutationTransformer"/>) shipped in earlier sub-tasks. These
/// tests pin the exact metadata documented in the membership-manager seed-sample
/// SQL headers and prove it produces the required behaviour:
///
///   members    policy-row-scope: user_id = {user_id}      policy-row-scope-roles: member
///   households policy-row-scope: household_id = {household_id}  policy-row-scope-roles: member
///
/// Acceptance criteria exercised:
///   - a <c>member</c> read on <c>members</c> is narrowed to their own row
///     (self-read); a query that would reach another member's row matches
///     nothing (cross-read returns empty);
///   - a <c>member</c> update on another member's row is denied server-side —
///     the mutation transformer scopes update/delete to the caller's own row,
///     so a cross-member write matches no row;
///   - <c>officer</c> (and every non-<c>member</c> role) is left unscoped on
///     these tables, so the officer lifecycle CRUD is unaffected;
///   - <c>admin</c> bypasses the row scope entirely.
/// </summary>
public class MembershipManagerRowScopePolicyTests
{
    // The verbatim row-scope configuration documented in the membership-manager
    // seed-sample SQL headers. Pinned here so a drift between the seed docs and
    // the engine behaviour fails a test.
    private const string MembersRowScope = "user_id = {user_id}";
    private const string HouseholdsRowScope = "household_id = {household_id}";
    private const string RowScopeRole = "member";

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

    private static IDictionary<string, object?> Caller(
        string role, string userId, int? householdId = null)
    {
        var context = new Dictionary<string, object?>
        {
            ["user_id"] = userId,
            ["roles"] = new[] { role },
        };
        if (householdId is not null)
            context["household_id"] = householdId;
        return context;
    }

    // Builds a model whose members and households tables carry exactly the
    // membership-manager seed-sample policy metadata.
    private static IDbModel MembershipManagerModel() =>
        DbModelTestFixture.Create()
            .WithTable("members", t => t
                .WithSchema("main")
                .WithPrimaryKey("member_id")
                .WithColumn("tenant_id", "int")
                .WithColumn("user_id", "int")
                .WithColumn("household_id", "int")
                .WithColumn("first_name", "varchar")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id")
                .WithMetadata(MetadataKeys.Policy.Actions, "read,create,update,delete")
                .WithMetadata(MetadataKeys.Policy.RowScope, MembersRowScope)
                .WithMetadata(MetadataKeys.Policy.RowScopeRoles, RowScopeRole))
            .WithTable("households", t => t
                .WithSchema("main")
                .WithPrimaryKey("household_id")
                .WithColumn("tenant_id", "int")
                .WithColumn("name", "varchar")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id")
                .WithMetadata(MetadataKeys.Policy.Actions, "read,create,update,delete")
                .WithMetadata(MetadataKeys.Policy.RowScope, HouseholdsRowScope)
                .WithMetadata(MetadataKeys.Policy.RowScopeRoles, RowScopeRole))
            .Build();

    // ---- Config collection: the seed metadata parses into a role-scoped policy ----

    [Fact]
    public void Config_MembersTable_CarriesMemberScopedRowScopeExpression()
    {
        var model = MembershipManagerModel();

        var policy = PolicyConfigCollector.FromTable(model.GetTableFromDbName("members"));

        policy.RowScopeExpression.Should().Be(MembersRowScope);
        policy.RowScopeRoles.Should().BeEquivalentTo(new[] { RowScopeRole });
    }

    [Fact]
    public void Config_HouseholdsTable_CarriesMemberScopedRowScopeExpression()
    {
        var model = MembershipManagerModel();

        var policy = PolicyConfigCollector.FromTable(model.GetTableFromDbName("households"));

        policy.RowScopeExpression.Should().Be(HouseholdsRowScope);
        policy.RowScopeRoles.Should().BeEquivalentTo(new[] { RowScopeRole });
    }

    // ---- Member self-read: the query is narrowed to the caller's own row ----

    [Fact]
    public void MemberQuery_OnMembers_IsNarrowedToTheCallersOwnRow()
    {
        var model = MembershipManagerModel();
        var transformer = new PolicyFilterTransformer();
        var context = QueryContext(model, Caller("member", userId: "42"));

        var filter = transformer.GetAdditionalFilter(
            model.GetTableFromDbName("members"), context);

        // Row scope compiles to `user_id = 42` — the member only ever sees the
        // members row linked to their own login account.
        filter.Should().NotBeNull();
        filter!.ColumnName.Should().Be("user_id");
        filter.Next!.RelationName.Should().Be("_eq");
        filter.Next.Value.Should().Be("42");
    }

    [Fact]
    public void MemberQuery_OnHouseholds_IsNarrowedToTheCallersOwnHousehold()
    {
        var model = MembershipManagerModel();
        var transformer = new PolicyFilterTransformer();
        var context = QueryContext(model, Caller("member", userId: "42", householdId: 7));

        var filter = transformer.GetAdditionalFilter(
            model.GetTableFromDbName("households"), context);

        filter.Should().NotBeNull();
        filter!.ColumnName.Should().Be("household_id");
        filter.Next!.RelationName.Should().Be("_eq");
        filter.Next.Value.Should().Be(7);
    }

    // ---- Member cross-read: a query for another member's row matches nothing ----

    [Fact]
    public void MemberQuery_RowScopeExcludesEveryRowButTheCallersOwn()
    {
        // The compiled filter is `user_id = {caller}`. A cross-member read — a
        // query that hopes to reach member user_id 99 while the caller is 42 —
        // is ANDed against `user_id = 42`, so it can never match user_id 99:
        // the cross-read returns empty rather than another member's data.
        var model = MembershipManagerModel();
        var transformer = new PolicyFilterTransformer();
        var context = QueryContext(model, Caller("member", userId: "42"));

        var filter = transformer.GetAdditionalFilter(
            model.GetTableFromDbName("members"), context);

        filter!.Next!.Value.Should().Be("42");
        filter.Next.Value.Should().NotBe("99",
            "the row scope pins the query to the caller, so another member's row is unreachable");
    }

    [Fact]
    public void MemberQuery_RowScopeIsAndedAlongsideTheTenantFilter()
    {
        // The row scope must narrow, not replace, the tenant filter: a member is
        // constrained to their own row AND their own tenant.
        var model = MembershipManagerModel();
        var transformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new TenantFilterTransformer(),
                new PolicyFilterTransformer(),
            },
        };
        var userContext = Caller("member", userId: "42");
        userContext["tenant_id"] = 1;

        var filter = transformers.GetCombinedFilter(
            model.GetTableFromDbName("members"), QueryContext(model, userContext));

        filter.Should().NotBeNull();
        filter!.FilterType.Should().Be(FilterType.And);
        filter.And.Should().HaveCount(2);
        filter.And[0].ColumnName.Should().Be("tenant_id");
        filter.And[1].ColumnName.Should().Be("user_id");
    }

    // ---- Member cross-write: an update on another member's row is denied ----

    [Fact]
    public async Task MemberUpdate_OnMembers_IsScopedToTheCallersOwnRow()
    {
        // PolicyMutationTransformer returns the row-scope filter as the
        // mutation's AdditionalFilter for update. A member updating another
        // member's profile matches no row once `user_id = {caller}` is ANDed in,
        // so the cross-member write is denied server-side.
        var model = MembershipManagerModel();
        var transformer = new PolicyMutationTransformer();
        var context = MutationContext(model, Caller("member", userId: "42"));

        var result = await transformer.TransformAsync(
            model.GetTableFromDbName("members"),
            MutationType.Update,
            new Dictionary<string, object?> { ["first_name"] = "Renamed" },
            context);

        result.Errors.Should().BeNullOrEmpty();
        result.AdditionalFilter.Should().NotBeNull();
        result.AdditionalFilter!.ColumnName.Should().Be("user_id");
        result.AdditionalFilter.Next!.RelationName.Should().Be("_eq");
        result.AdditionalFilter.Next.Value.Should().Be("42");
    }

    [Fact]
    public async Task MemberDelete_OnMembers_IsScopedToTheCallersOwnRow()
    {
        var model = MembershipManagerModel();
        var transformer = new PolicyMutationTransformer();
        var context = MutationContext(model, Caller("member", userId: "42"));

        var result = await transformer.TransformAsync(
            model.GetTableFromDbName("members"),
            MutationType.Delete,
            new Dictionary<string, object?>(),
            context);

        result.Errors.Should().BeNullOrEmpty();
        result.AdditionalFilter.Should().NotBeNull();
        result.AdditionalFilter!.ColumnName.Should().Be("user_id");
        result.AdditionalFilter.Next!.Value.Should().Be("42");
    }

    // ---- officer and every non-member role stay unscoped on these tables ----

    [Fact]
    public void OfficerQuery_OnMembers_IsNotNarrowedByTheRowScope()
    {
        // policy-row-scope-roles: member qualifies the scope to the member role.
        // An officer holds a different role, so the row-scope filter does not
        // apply — the officer keeps full-table lifecycle access (still bounded
        // by the tenant filter).
        var model = MembershipManagerModel();
        var transformer = new PolicyFilterTransformer();
        var context = QueryContext(model, Caller("officer", userId: "7"));

        transformer.GetAdditionalFilter(model.GetTableFromDbName("members"), context)
            .Should().BeNull("officer is not the member role, so it is not row-scoped");
    }

    [Fact]
    public async Task OfficerUpdate_OnMembers_IsNotScopedSoCrossMemberEditsArePermitted()
    {
        var model = MembershipManagerModel();
        var transformer = new PolicyMutationTransformer();
        var context = MutationContext(model, Caller("officer", userId: "7"));

        var result = await transformer.TransformAsync(
            model.GetTableFromDbName("members"),
            MutationType.Update,
            new Dictionary<string, object?> { ["first_name"] = "Renamed" },
            context);

        result.Errors.Should().BeNullOrEmpty();
        result.AdditionalFilter.Should().BeNull(
            "officer manages the full member lifecycle and is not narrowed to its own row");
    }

    [Theory]
    [InlineData("event_manager")]
    [InlineData("finance_manager")]
    [InlineData("read_only")]
    public void NonMemberRoles_AreNotNarrowedByTheRowScope(string role)
    {
        var model = MembershipManagerModel();
        var transformer = new PolicyFilterTransformer();
        var context = QueryContext(model, Caller(role, userId: "7"));

        transformer.GetAdditionalFilter(model.GetTableFromDbName("members"), context)
            .Should().BeNull($"{role} is not the member role, so it is not row-scoped");
    }

    // ---- admin bypasses the row scope entirely ----

    [Fact]
    public void AdminQuery_OnMembers_BypassesTheRowScope()
    {
        var model = MembershipManagerModel();
        var transformer = new PolicyFilterTransformer();
        var context = QueryContext(model, Caller("admin", userId: "1"));

        transformer.GetAdditionalFilter(model.GetTableFromDbName("members"), context)
            .Should().BeNull("admin bypasses the policy engine, including the row scope");
    }
}
