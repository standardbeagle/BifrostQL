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
/// Integration tests for the workflow-mutation audit trail (see the
/// "Workflow Mutations &amp; Audit Trail" guide). They prove the acceptance
/// criterion: audit entries are queryable through Bifrost and filtered by
/// tenant.
///
/// The <c>audit_log</c> table mirrors src/BifrostQL.UI/Schemas/org-model.sql and
/// its sample seed. It is a plain tenant-scoped application table — it carries
/// <c>tenant-filter: tenant_id</c> and <c>auto-filter: tenant_id:tenant_ids</c>
/// exactly like app_users / organization_memberships / invitations — so the
/// audit trail needs no custom resolver code: the same security-module
/// composition that isolates the rest of the org model isolates the audit log.
///
/// Per the existing integration-test convention (OrgModelCompositionTests),
/// the model is built in-code via DbModelTestFixture and assertions are made
/// against the generated SQL / combined filter tree rather than executing
/// against a live DB.
/// </summary>
public class WorkflowAuditCompositionTests
{
    private static readonly ISqlDialect Dialect = SqliteDialect.Instance;

    [Fact]
    public void AuditLog_IsQueryableThroughBifrost_AsAnOrdinaryTable()
    {
        // Arrange: the audit_log table is present in the generated model and its
        // workflow-relevant columns are selectable like any other table.
        var model = CreateOrgModel();
        var table = model.GetTableFromDbName("audit_log");
        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("audit_id", "actor_user_id", "action", "entity_type", "entity_id", "summary")
            .Build();

        // Act: a normal Bifrost query against the audit table.
        service.ApplyTransformers(query, model, OrgContext(tenantId: 1));
        var (sql, _) = GenerateSql(query, model);

        // Assert: the audit trail is read through the standard query path — no
        // custom resolver, the SQL selects the audit columns directly.
        sql.Should().Contain("\"audit_log\"");
        sql.Should().Contain("\"action\"");
        sql.Should().Contain("\"entity_type\"");
        sql.Should().Contain("\"summary\"");
    }

    [Fact]
    public void AuditLog_TenantFilter_ScopesEntriesToCallersTenant()
    {
        // Arrange: caller is in tenant 1; audit_log carries tenant-filter.
        var model = CreateOrgModel();
        var table = model.GetTableFromDbName("audit_log");
        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("audit_id", "action", "summary")
            .Build();

        // Act: tenant context pins the caller to tenant 1.
        service.ApplyTransformers(query, model, OrgContext(tenantId: 1));
        var (sql, parameters) = GenerateSql(query, model);

        // Assert: every audit read is constrained to tenant_id = 1, so another
        // tenant's audit entries are unreachable.
        sql.Should().Contain("WHERE");
        sql.Should().Contain("\"tenant_id\"");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, 1),
            "the caller's tenant id must be bound as a parameter");
        parameters.Parameters.Should().NotContain(p => Equals(p.Value, 2),
            "nothing should ever widen the audit query to another tenant");
    }

    [Fact]
    public void AuditLog_TenantPlusAutoFilter_ComposeIntoCombinedWhere()
    {
        // Arrange: audit_log carries both tenant-filter (priority 0) and
        // auto-filter tenant_id:tenant_ids (priority 1), matching the other
        // tenant-scoped org tables. Caller belongs to tenant {1}.
        var model = CreateOrgModel();
        var table = model.GetTableFromDbName("audit_log");
        var service = CreateTransformerService(
            new TenantFilterTransformer(),
            new AutoFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("audit_id", "action")
            .Build();

        // Act: both security transformers run for the one audit query.
        var context = OrgContext(tenantId: 1, tenantIds: new object[] { 1 });
        service.ApplyTransformers(query, model, context);
        var (sql, parameters) = GenerateSql(query, model);

        // Assert: a single combined WHERE references tenant_id from both the
        // tenant-filter and the auto-filter membership claim — the audit log is
        // isolated by the same composition as the rest of the org model.
        query.Filter.Should().NotBeNull();
        query.Filter!.FilterType.Should().Be(FilterType.And,
            "tenant-filter and auto-filter must AND-compose into one filter tree");
        query.Filter.And.Should().HaveCount(2);
        query.Filter.And[0].ColumnName.Should().Be("tenant_id");
        query.Filter.And[1].ColumnName.Should().Be("tenant_id");
        sql.Should().Contain("\"tenant_id\"");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, 1));
    }

    [Fact]
    public void AuditLog_AutoFilter_MultiTenantActor_ProducesInClauseAcrossTheirTenants()
    {
        // Arrange: an actor (e.g. a support admin) who belongs to two
        // organizations. The plural tenant_ids claim drives an IN filter so they
        // see audit entries for both — and only those two.
        var model = CreateOrgModel();
        var table = model.GetTableFromDbName("audit_log");
        var service = CreateTransformerService(
            new TenantFilterTransformer(),
            new AutoFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("audit_id", "tenant_id", "action")
            .Build();

        // Act: tenant context pins tenant 1; auto-filter widens audit reads to
        // {1,2} — the tenants the actor actually belongs to.
        var context = OrgContext(tenantId: 1, tenantIds: new object[] { 1, 2 });
        service.ApplyTransformers(query, model, context);
        var (sql, _) = GenerateSql(query, model);

        // Assert: the array tenant_ids claim produces an IN constraint on the
        // audit query.
        query.Filter.Should().NotBeNull();
        query.Filter!.And[1].Next!.RelationName.Should().Be("_in",
            "an array tenant_ids claim must produce an IN filter on the audit log");
        sql.Should().Contain("\"tenant_id\"");
        sql.Should().Contain(" IN ", "the combined audit SQL must include the IN clause");
    }

    [Fact]
    public void AuditLog_FailsClosed_WhenTenantContextMissing()
    {
        // Arrange: no tenant context at all.
        var model = CreateOrgModel();
        var service = CreateTransformerService(new TenantFilterTransformer());
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(model.GetTableFromDbName("audit_log"))
            .WithColumns("audit_id")
            .Build();

        // Act + Assert: a missing tenant context must fail closed, not return
        // every tenant's audit entries.
        var act = () => service.ApplyTransformers(query, model, new Dictionary<string, object?>());
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*Tenant context required*");
    }

    #region Helper Methods

    /// <summary>
    /// Builds an in-code model mirroring src/BifrostQL.UI/Schemas/org-model.sql,
    /// focused on the audit_log table plus the tenants table it references.
    /// audit_log carries tenant-filter + auto-filter — the same metadata recipe
    /// as the other tenant-scoped org tables.
    /// </summary>
    private static IDbModel CreateOrgModel()
    {
        return DbModelTestFixture.Create()
            // tenants: the org row itself — auto-filter on its own id, no tenant-filter.
            .WithTable("tenants", t => t
                .WithSchema("main")
                .WithPrimaryKey("tenant_id")
                .WithColumn("name", "text")
                .WithColumn("slug", "text")
                .WithMetadata("auto-filter", "tenant_id:tenant_ids"))
            // audit_log: tenant-scoped application table — tenant-filter + auto-filter.
            .WithTable("audit_log", t => t
                .WithSchema("main")
                .WithPrimaryKey("audit_id")
                .WithColumn("tenant_id", "int")
                .WithColumn("actor_user_id", "int")
                .WithColumn("action", "text")
                .WithColumn("entity_type", "text")
                .WithColumn("entity_id", "text")
                .WithColumn("summary", "text")
                .WithColumn("created_at", "text")
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
