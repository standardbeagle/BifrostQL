using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Integration tests proving the reusable org-model app schema enforces org/tenant
/// isolation on read paths using ONLY <c>tenant-filter</c> + <c>auto-filter</c> metadata
/// composition — no custom resolver code.
///
/// The schema mirrors src/BifrostQL.UI/Schemas/org-model.sql and its sample seed
/// (split 2): tenants, app_users, organization_memberships, invitations are
/// tenant-scoped; roles / role_permissions are global lookups. Tenant-scoped tables
/// carry <c>tenant-filter: tenant_id</c> and <c>auto-filter: tenant_id:tenant_ids</c>,
/// so a caller only sees rows for organizations they belong to.
///
/// Per the existing integration-test convention (SqliteTenantFilterIntegrationTests),
/// the model is built in-code via DbModelTestFixture and assertions are made against
/// the generated SQL / combined filter tree rather than executing against a live DB.
/// </summary>
public class OrgModelCompositionTests
{
    private static readonly ISqlDialect Dialect = SqliteDialect.Instance;

    #region Tenant Isolation (tenant-filter alone)

    [Fact]
    public void TenantFilter_UserInTenant1_CannotSeeTenant2Rows()
    {
        // Arrange: org-model schema, caller is Alice in tenant 1.
        var model = CreateOrgModel();
        var table = model.GetTableFromDbName("app_users");
        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("user_id", "email", "display_name")
            .Build();

        // Act: tenant context pins the caller to tenant 1.
        service.ApplyTransformers(query, model, OrgContext(tenantId: 1));
        var (sql, parameters) = GenerateSql(query, model);

        // Assert: every read is constrained to tenant_id = 1, so tenant 2 rows
        // (dave, erin) are unreachable.
        sql.Should().Contain("WHERE");
        sql.Should().Contain("\"tenant_id\"");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, 1),
            "the caller's tenant id must be bound as a parameter");
        parameters.Parameters.Should().NotContain(p => Equals(p.Value, 2),
            "nothing should ever widen the query to tenant 2");
    }

    [Fact]
    public void TenantFilter_AppliesToEveryTenantScopedOrgTable()
    {
        // Arrange: all tenant-scoped tables carry tenant-filter metadata.
        var model = CreateOrgModel();
        var transformer = new TenantFilterTransformer();
        var tenantScoped = new[] { "app_users", "organization_memberships", "invitations" };

        foreach (var name in tenantScoped)
        {
            var table = model.GetTableFromDbName(name);
            var context = OrgTransformContext(model, OrgContext(tenantId: 1));

            // Act + Assert: tenant filter binds tenant_id on each scoped table.
            transformer.AppliesTo(table, context).Should().BeTrue(
                $"tenant-filter must apply to tenant-scoped table {name}");
            var filter = transformer.GetAdditionalFilter(table, context);
            filter.Should().NotBeNull();
            filter!.ColumnName.Should().Be("tenant_id");
            filter.Next!.Value.Should().Be(1);
        }
    }

    [Fact]
    public void TenantFilter_DoesNotApplyToGlobalLookupTables()
    {
        // Arrange: roles / role_permissions are global lookups — no tenant-filter.
        var model = CreateOrgModel();
        var service = CreateTransformerService(new TenantFilterTransformer());

        foreach (var name in new[] { "roles", "role_permissions" })
        {
            var table = model.GetTableFromDbName(name);
            var query = GqlObjectQueryBuilder.Create()
                .WithDbTable(table)
                .WithColumns("role_id")
                .Build();

            // Act: even with a tenant context present, lookups stay un-scoped.
            service.ApplyTransformers(query, model, OrgContext(tenantId: 1));

            // Assert: no filter injected on global lookup tables.
            query.Filter.Should().BeNull(
                $"global lookup table {name} must not receive a tenant filter");
        }
    }

    #endregion

    #region Tenant + Auto-Filter Composition

    [Fact]
    public void TenantPlusAutoFilter_ComposeIntoCombinedWhereForSameQuery()
    {
        // Arrange: app_users carries both tenant-filter (priority 0) and
        // auto-filter tenant_id:tenant_ids (priority 1). Caller belongs to
        // tenants {1} only.
        var model = CreateOrgModel();
        var table = model.GetTableFromDbName("app_users");
        var service = CreateTransformerService(
            new TenantFilterTransformer(),
            new AutoFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("user_id", "email")
            .Build();

        // Act: both security transformers run for the one query.
        var context = OrgContext(tenantId: 1, tenantIds: new object[] { 1 });
        service.ApplyTransformers(query, model, context);
        var (sql, parameters) = GenerateSql(query, model);

        // Assert: a single combined WHERE references tenant_id from both the
        // tenant-filter and the auto-filter membership claim.
        query.Filter.Should().NotBeNull();
        query.Filter!.FilterType.Should().Be(FilterType.And,
            "tenant-filter and auto-filter must AND-compose into one filter tree");
        query.Filter.And.Should().HaveCount(2);
        query.Filter.And[0].ColumnName.Should().Be("tenant_id",
            "tenant-filter (priority 0) composes first");
        query.Filter.And[1].ColumnName.Should().Be("tenant_id",
            "auto-filter (priority 1) composes second");
        sql.Should().Contain("WHERE");
        sql.Should().Contain("\"tenant_id\"");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, 1));
    }

    [Fact]
    public void AutoFilter_MultiTenantMembership_ProducesInClauseAcrossCallerTenants()
    {
        // Arrange: a caller belonging to two organizations. The plural tenant_ids
        // claim drives an IN filter so they see rows for both.
        var model = CreateOrgModel();
        var table = model.GetTableFromDbName("organization_memberships");
        var service = CreateTransformerService(
            new TenantFilterTransformer(),
            new AutoFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("membership_id", "user_id", "role_id")
            .Build();

        // Act: tenant context pins tenant 1; auto-filter widens reads to {1,2}
        // memberships the caller actually holds.
        var context = OrgContext(tenantId: 1, tenantIds: new object[] { 1, 2 });
        service.ApplyTransformers(query, model, context);
        var (sql, _) = GenerateSql(query, model);

        // Assert: auto-filter array claim produces an IN constraint.
        query.Filter.Should().NotBeNull();
        query.Filter!.And[1].Next!.RelationName.Should().Be("_in",
            "an array tenant_ids claim must produce an IN filter, not equality");
        sql.Should().Contain("\"tenant_id\"");
        sql.Should().Contain(" IN ", "the combined SQL must include the IN clause");
    }

    #endregion

    #region Admin Bypass Role

    [Fact]
    public void AdminBypassRole_SkipsAutoFilter_ButTenantFilterStillApplies()
    {
        // Arrange: model configures auto-filter-bypass-role = owner. The caller is
        // an owner, so auto-filter is skipped — but tenant-filter is a separate
        // transformer with no bypass, so tenant isolation still holds.
        var model = CreateOrgModel(bypassRole: "owner");
        var table = model.GetTableFromDbName("app_users");
        var service = CreateTransformerService(
            new TenantFilterTransformer(),
            new AutoFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("user_id", "email")
            .Build();

        // Act: owner caller in tenant 1.
        var context = OrgContext(tenantId: 1, tenantIds: new object[] { 1 });
        context["roles"] = new[] { "owner" };
        service.ApplyTransformers(query, model, context);
        var (sql, parameters) = GenerateSql(query, model);

        // Assert: only the tenant-filter remains — a single tenant_id constraint,
        // not an AND of two. Bypass scopes auto-filter only, tenant isolation stays.
        query.Filter.Should().NotBeNull();
        query.Filter!.ColumnName.Should().Be("tenant_id",
            "tenant-filter still applies even for the auto-filter bypass role");
        query.Filter.FilterType.Should().NotBe(FilterType.And,
            "auto-filter must be skipped for the bypass role, leaving only tenant-filter");
        sql.Should().Contain("\"tenant_id\"");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, 1));
    }

    [Fact]
    public void NonBypassRole_StillGetsBothTenantAndAutoFilter()
    {
        // Arrange: same bypass config, but caller is a plain member (not owner).
        var model = CreateOrgModel(bypassRole: "owner");
        var table = model.GetTableFromDbName("app_users");
        var service = CreateTransformerService(
            new TenantFilterTransformer(),
            new AutoFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("user_id")
            .Build();

        // Act: member caller — does not hold the bypass role.
        var context = OrgContext(tenantId: 1, tenantIds: new object[] { 1 });
        context["roles"] = new[] { "member" };
        service.ApplyTransformers(query, model, context);

        // Assert: both transformers compose for a non-bypass caller.
        query.Filter.Should().NotBeNull();
        query.Filter!.FilterType.Should().Be(FilterType.And,
            "a non-bypass caller must receive both tenant-filter and auto-filter");
        query.Filter.And.Should().HaveCount(2);
    }

    #endregion

    #region Club-Per-Tenant Read Path

    [Fact]
    public void ClubPerTenantPath_ReturnsOnlyCallersClubRows()
    {
        // Arrange: a "club" is an organization (tenant) row. The tenants table is
        // the row's own identity, so it is auto-filtered on its own id via
        // auto-filter tenant_id:tenant_ids. Caller belongs only to club 1.
        var model = CreateOrgModel();
        var table = model.GetTableFromDbName("tenants");
        var service = CreateTransformerService(
            new TenantFilterTransformer(),
            new AutoFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("tenant_id", "name", "slug")
            .Build();

        // Act: caller's membership claim is {1}.
        var context = OrgContext(tenantId: 1, tenantIds: new object[] { 1 });
        service.ApplyTransformers(query, model, context);
        var (sql, parameters) = GenerateSql(query, model);

        // Assert: the tenants/clubs query is constrained to the caller's club id;
        // tenant 2 ("Globex") is unreachable. tenants carries no tenant-filter
        // (it is the tenant itself) so only the auto-filter applies.
        query.Filter.Should().NotBeNull();
        query.Filter!.ColumnName.Should().Be("tenant_id");
        sql.Should().Contain("\"tenant_id\"");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, 1));
        parameters.Parameters.Should().NotContain(p => Equals(p.Value, 2),
            "the caller must not be able to read another club's tenant row");
    }

    #endregion

    #region Empty / Missing Claim Error Cases

    [Fact]
    public void TenantFilter_ThrowsWhenTenantContextMissing()
    {
        // Arrange: no tenant context at all.
        var model = CreateOrgModel();
        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(model.GetTableFromDbName("app_users"))
            .WithColumns("user_id")
            .Build();

        // Act + Assert: missing tenant context must fail closed, not return all rows.
        var act = () => service.ApplyTransformers(query, model, new Dictionary<string, object?>());
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*Tenant context required*");
    }

    [Fact]
    public void AutoFilter_ThrowsWhenMembershipClaimMissing()
    {
        // Arrange: tenant context present, but the tenant_ids membership claim
        // that auto-filter depends on is absent.
        var model = CreateOrgModel();
        var service = CreateTransformerService(
            new TenantFilterTransformer(),
            new AutoFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(model.GetTableFromDbName("app_users"))
            .WithColumns("user_id")
            .Build();

        // Act + Assert: a missing auto-filter claim must fail closed.
        var contextMissingClaim = new Dictionary<string, object?> { ["tenant_id"] = 1 };
        var act = () => service.ApplyTransformers(query, model, contextMissingClaim);
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*tenant_ids*")
            .WithMessage("*required but not found*");
    }

    [Fact]
    public void AutoFilter_ThrowsWhenMembershipClaimEmpty()
    {
        // Arrange: caller belongs to zero organizations — empty tenant_ids claim.
        var model = CreateOrgModel();
        var service = CreateTransformerService(
            new TenantFilterTransformer(),
            new AutoFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(model.GetTableFromDbName("app_users"))
            .WithColumns("user_id")
            .Build();

        // Act + Assert: an empty membership set must fail closed rather than
        // produce an empty IN that silently matches nothing in an ambiguous way.
        var context = OrgContext(tenantId: 1, tenantIds: Array.Empty<object>());
        var act = () => service.ApplyTransformers(query, model, context);
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*tenant_ids*")
            .WithMessage("*cannot be empty*");
    }

    [Fact]
    public void AutoFilter_ThrowsWhenMembershipClaimNull()
    {
        // Arrange: tenant_ids claim present but null.
        var model = CreateOrgModel();
        var service = CreateTransformerService(
            new TenantFilterTransformer(),
            new AutoFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(model.GetTableFromDbName("app_users"))
            .WithColumns("user_id")
            .Build();

        // Act + Assert: null membership claim must fail closed.
        var context = new Dictionary<string, object?> { ["tenant_id"] = 1, ["tenant_ids"] = null };
        var act = () => service.ApplyTransformers(query, model, context);
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*tenant_ids*")
            .WithMessage("*cannot be null*");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Builds an in-code model mirroring src/BifrostQL.UI/Schemas/org-model.sql with
    /// the recommended metadata: tenant-scoped tables carry tenant-filter + auto-filter,
    /// global lookups carry neither. tenants carries auto-filter only (it is the tenant
    /// row itself). Optionally configures an auto-filter bypass role.
    /// </summary>
    private static IDbModel CreateOrgModel(string? bypassRole = null)
    {
        var builder = DbModelTestFixture.Create();
        if (bypassRole != null)
            builder = builder.WithModelMetadata("auto-filter-bypass-role", bypassRole);

        return builder
            // tenants: the org/club row itself — auto-filter on its own id, no tenant-filter.
            .WithTable("tenants", t => t
                .WithSchema("main")
                .WithPrimaryKey("tenant_id")
                .WithColumn("name", "text")
                .WithColumn("slug", "text")
                .WithColumn("plan", "text")
                .WithColumn("is_active", "int")
                .WithMetadata("auto-filter", "tenant_id:tenant_ids"))
            // Global lookups — un-scoped.
            .WithTable("roles", t => t
                .WithSchema("main")
                .WithPrimaryKey("role_id")
                .WithColumn("name", "text")
                .WithColumn("description", "text")
                .WithColumn("is_system", "int"))
            .WithTable("role_permissions", t => t
                .WithSchema("main")
                .WithPrimaryKey("role_permission_id")
                .WithColumn("role_id", "int")
                .WithColumn("permission", "text"))
            // Tenant-scoped tables — tenant-filter + auto-filter composition.
            .WithTable("app_users", t => t
                .WithSchema("main")
                .WithPrimaryKey("user_id")
                .WithColumn("tenant_id", "int")
                .WithColumn("email", "text")
                .WithColumn("display_name", "text")
                .WithColumn("is_active", "int")
                .WithMetadata("tenant-filter", "tenant_id")
                .WithMetadata("auto-filter", "tenant_id:tenant_ids"))
            .WithTable("organization_memberships", t => t
                .WithSchema("main")
                .WithPrimaryKey("membership_id")
                .WithColumn("tenant_id", "int")
                .WithColumn("user_id", "int")
                .WithColumn("role_id", "int")
                .WithColumn("status", "text")
                .WithMetadata("tenant-filter", "tenant_id")
                .WithMetadata("auto-filter", "tenant_id:tenant_ids"))
            .WithTable("invitations", t => t
                .WithSchema("main")
                .WithPrimaryKey("invitation_id")
                .WithColumn("tenant_id", "int")
                .WithColumn("email", "text")
                .WithColumn("role_id", "int")
                .WithColumn("status", "text")
                .WithMetadata("tenant-filter", "tenant_id")
                .WithMetadata("auto-filter", "tenant_id:tenant_ids"))
            .Build();
    }

    private static IDictionary<string, object?> OrgContext(object tenantId, object? tenantIds = null)
    {
        var ctx = new Dictionary<string, object?> { ["tenant_id"] = tenantId };
        if (tenantIds != null)
            ctx["tenant_ids"] = tenantIds;
        return ctx;
    }

    private static QueryTransformContext OrgTransformContext(
        IDbModel model, IDictionary<string, object?> userContext)
    {
        return new QueryTransformContext
        {
            Model = model,
            UserContext = userContext,
            QueryType = QueryType.Standard,
            Path = "",
            IsNestedQuery = false
        };
    }

    private static (string sql, SqlParameterCollection parameters) GenerateSql(
        GqlObjectQuery query, IDbModel model)
    {
        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);
        var sql = sqls.Values.First().Sql;
        return (sql, parameters);
    }

    private static QueryTransformerService CreateTransformerService(params IFilterTransformer[] transformers)
    {
        var wrap = new FilterTransformersWrap { Transformers = transformers };
        return new QueryTransformerService(wrap);
    }

    #endregion
}
