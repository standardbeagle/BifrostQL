using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// RED/GREEN TDD coverage for <see cref="PolicyFilterTransformer"/>, the
/// query-path enforcement point for the server-side authorization policy
/// engine (sub-task 2/4).
///
/// The transformer:
///   - throws <see cref="BifrostExecutionError"/> for a table the caller has
///     no <c>read</c> permission for;
///   - compiles the table's row-scope expression into a <see cref="TableFilter"/>
///     that is ANDed alongside the tenant filter (never replacing it);
///   - exposes <see cref="PolicyFilterTransformer.AssertColumnsReadable"/> as the
///     column-read-deny enforcement seam (chosen mechanism: reject a query that
///     references a denied column with a clear, non-leaking error).
///
/// Identity is read from the per-request user context: roles from <c>roles</c>
/// and the user id from <c>user_id</c> — the canonical claims
/// <c>IdentityContextMapper</c> writes.
/// </summary>
public class PolicyFilterTransformerTests
{
    private static QueryTransformContext Context(
        IDbModel model, IDictionary<string, object?>? userContext = null) =>
        new()
        {
            Model = model,
            UserContext = userContext ?? new Dictionary<string, object?>(),
            QueryType = QueryType.Standard,
            Path = "",
            IsNestedQuery = false,
        };

    private static IDictionary<string, object?> UserWithRoles(params string[] roles) =>
        new Dictionary<string, object?>
        {
            ["user_id"] = "user-1",
            ["roles"] = roles,
        };

    private static IDbModel ModelWithPolicy(params (string key, string value)[] metadata)
    {
        var builder = DbModelTestFixture.Create()
            .WithTable("Orders", t =>
            {
                t.WithSchema("dbo")
                    .WithPrimaryKey("Id")
                    .WithColumn("tenant_id", "int")
                    .WithColumn("ssn", "varchar")
                    .WithColumn("Total", "decimal");
                foreach (var (key, value) in metadata)
                    t.WithMetadata(key, value);
            });
        return builder.Build();
    }

    // ---- AppliesTo ----

    [Fact]
    public void AppliesTo_TrueWhenTableHasPolicyMetadata()
    {
        var model = ModelWithPolicy((MetadataKeys.Policy.Actions, "read"));
        var transformer = new PolicyFilterTransformer();

        transformer.AppliesTo(model.GetTableFromDbName("Orders"), Context(model))
            .Should().BeTrue();
    }

    [Fact]
    public void AppliesTo_FalseWhenTableHasNoPolicyMetadata()
    {
        var model = ModelWithPolicy();
        var transformer = new PolicyFilterTransformer();

        transformer.AppliesTo(model.GetTableFromDbName("Orders"), Context(model))
            .Should().BeFalse();
    }

    // ---- Priority ----

    [Fact]
    public void Priority_IsInSecurityRange_AfterTenantFilter()
    {
        var transformer = new PolicyFilterTransformer();
        transformer.Priority.Should().BeInRange(0, 99);
        transformer.Priority.Should().BeGreaterThan(new TenantFilterTransformer().Priority);
    }

    // ---- Table read deny ----

    [Fact]
    public void GetAdditionalFilter_TableReadDenied_ThrowsNonLeakingError()
    {
        // Policy permits only update — read is not allowed.
        var model = ModelWithPolicy((MetadataKeys.Policy.Actions, "update"));
        var transformer = new PolicyFilterTransformer();
        var context = Context(model, UserWithRoles("user"));

        var ex = Assert.Throws<BifrostExecutionError>(() =>
            transformer.GetAdditionalFilter(model.GetTableFromDbName("Orders"), context));

        ex.Message.Should().NotContain("Orders");
        ex.Message.Should().NotContain("read");
    }

    [Fact]
    public void GetAdditionalFilter_TableReadAllowed_ReturnsNullWhenNoRowScope()
    {
        var model = ModelWithPolicy((MetadataKeys.Policy.Actions, "read"));
        var transformer = new PolicyFilterTransformer();
        var context = Context(model, UserWithRoles("user"));

        transformer.GetAdditionalFilter(model.GetTableFromDbName("Orders"), context)
            .Should().BeNull();
    }

    [Fact]
    public void GetAdditionalFilter_AdminRole_BypassesTableReadDeny()
    {
        var model = ModelWithPolicy((MetadataKeys.Policy.Actions, "update"));
        var transformer = new PolicyFilterTransformer();
        var context = Context(model, UserWithRoles("admin"));

        // Admin bypass: no throw, and no row-scope filter to add.
        transformer.GetAdditionalFilter(model.GetTableFromDbName("Orders"), context)
            .Should().BeNull();
    }

    // ---- Row-scope ----

    [Fact]
    public void GetAdditionalFilter_RowScope_CompilesToEqualityFilter()
    {
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, "read"),
            (MetadataKeys.Policy.RowScope, "tenant_id = {tenant_id}"));
        var transformer = new PolicyFilterTransformer();
        var userContext = UserWithRoles("user");
        userContext["tenant_id"] = 7;
        var context = Context(model, userContext);

        var filter = transformer.GetAdditionalFilter(
            model.GetTableFromDbName("Orders"), context);

        filter.Should().NotBeNull();
        filter!.ColumnName.Should().Be("tenant_id");
        filter.Next!.RelationName.Should().Be("_eq");
        filter.Next.Value.Should().Be(7);
    }

    [Fact]
    public void GetAdditionalFilter_AdminRole_SkipsRowScopeFilter()
    {
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, "read"),
            (MetadataKeys.Policy.RowScope, "tenant_id = {tenant_id}"));
        var transformer = new PolicyFilterTransformer();
        var userContext = UserWithRoles("admin");
        userContext["tenant_id"] = 7;
        var context = Context(model, userContext);

        transformer.GetAdditionalFilter(model.GetTableFromDbName("Orders"), context)
            .Should().BeNull();
    }

    // ---- Tenant-filter integration ----

    [Fact]
    public void Integration_RowScopeIsAndedAlongsideTenantFilter()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("tenant_id", "int")
                .WithColumn("region_id", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id")
                .WithMetadata(MetadataKeys.Policy.Actions, "read")
                .WithMetadata(MetadataKeys.Policy.RowScope, "region_id = {region_id}"))
            .Build();

        var transformers = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new TenantFilterTransformer(),
                new PolicyFilterTransformer(),
            },
        };

        var userContext = UserWithRoles("user");
        userContext["tenant_id"] = 1;
        userContext["region_id"] = 9;

        var filter = transformers.GetCombinedFilter(
            model.GetTableFromDbName("Orders"), Context(model, userContext));

        // AND of both filters: tenant filter (priority 0) then policy row-scope.
        filter.Should().NotBeNull();
        filter!.FilterType.Should().Be(FilterType.And);
        filter.And.Should().HaveCount(2);
        filter.And[0].ColumnName.Should().Be("tenant_id");
        filter.And[1].ColumnName.Should().Be("region_id");
    }

    // ---- Column read-deny (chosen mechanism: reject with clear error) ----

    [Fact]
    public void AssertColumnsReadable_DeniedColumnReferenced_ThrowsNonLeakingError()
    {
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, "read"),
            (MetadataKeys.Policy.ReadDeny, "ssn"));
        var transformer = new PolicyFilterTransformer();
        var context = Context(model, UserWithRoles("user"));

        var ex = Assert.Throws<BifrostExecutionError>(() =>
            transformer.AssertColumnsReadable(
                model.GetTableFromDbName("Orders"),
                new[] { "Total", "ssn" },
                context));

        ex.Message.Should().NotContain("ssn");
        ex.Message.Should().NotContain("Orders");
    }

    [Fact]
    public void AssertColumnsReadable_OnlyAllowedColumns_DoesNotThrow()
    {
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, "read"),
            (MetadataKeys.Policy.ReadDeny, "ssn"));
        var transformer = new PolicyFilterTransformer();
        var context = Context(model, UserWithRoles("user"));

        var act = () => transformer.AssertColumnsReadable(
            model.GetTableFromDbName("Orders"),
            new[] { "Total", "tenant_id" },
            context);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertColumnsReadable_AdminRole_BypassesColumnDeny()
    {
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, "read"),
            (MetadataKeys.Policy.ReadDeny, "ssn"));
        var transformer = new PolicyFilterTransformer();
        var context = Context(model, UserWithRoles("admin"));

        var act = () => transformer.AssertColumnsReadable(
            model.GetTableFromDbName("Orders"),
            new[] { "ssn" },
            context);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertColumnsReadable_NoPolicyMetadata_DoesNotThrow()
    {
        // Absent-policy default established in sub-task 1 is ALLOW.
        var model = ModelWithPolicy();
        var transformer = new PolicyFilterTransformer();
        var context = Context(model, UserWithRoles("user"));

        var act = () => transformer.AssertColumnsReadable(
            model.GetTableFromDbName("Orders"),
            new[] { "ssn", "Total" },
            context);

        act.Should().NotThrow();
    }
}
