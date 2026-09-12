using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// RED/GREEN TDD coverage for <see cref="PolicyMutationTransformer"/>, the
/// mutation-path enforcement point for the server-side authorization policy
/// engine (sub-task 3/4).
///
/// The transformer:
///   - rejects a create/update/delete on a table the caller lacks the matching
///     action permission for, via <see cref="MutationTransformResult.Errors"/>
///     (which aborts the mutation) with a generic, non-leaking message;
///   - rejects a mutation whose data dictionary writes a write-denied column,
///     the same way;
///   - compiles the table's row-scope expression into a <see cref="TableFilter"/>
///     returned as <see cref="MutationTransformResult.AdditionalFilter"/> on
///     update/delete (never on create), so the wrap ANDs it alongside the
///     tenant filter rather than replacing it;
///   - lets an admin-role caller through every check.
///
/// Identity is read from the per-request user context: roles from <c>roles</c>
/// and the user id from <c>user_id</c> — the canonical claims
/// <c>IdentityContextMapper</c> writes.
/// </summary>
public class PolicyMutationTransformerTests
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

    private static IDbModel ModelWithPolicy(params (string key, string value)[] metadata)
    {
        var builder = DbModelTestFixture.Create()
            .WithTable("Orders", t =>
            {
                t.WithSchema("dbo")
                    .WithPrimaryKey("Id")
                    .WithColumn("tenant_id", "int")
                    .WithColumn("ssn", "varchar")
                    .WithColumn("Total", "decimal")
                    .WithColumn("deleted_at", "datetime");
                foreach (var (key, value) in metadata)
                    t.WithMetadata(key, value);
            });
        return builder.Build();
    }

    private static IDbTable Orders(IDbModel model) => model.GetTableFromDbName("Orders");

    // ---- AppliesTo ----

    [Fact]
    public void AppliesTo_TrueWhenTableHasPolicyMetadata()
    {
        var model = ModelWithPolicy((MetadataKeys.Policy.Actions, "create"));
        var transformer = new PolicyMutationTransformer();

        transformer.AppliesTo(Orders(model), MutationType.Insert, Context(model))
            .Should().BeTrue();
    }

    [Fact]
    public void AppliesTo_FalseWhenTableHasNoPolicyMetadata()
    {
        var model = ModelWithPolicy();
        var transformer = new PolicyMutationTransformer();

        transformer.AppliesTo(Orders(model), MutationType.Insert, Context(model))
            .Should().BeFalse();
    }

    // ---- Priority ----

    [Fact]
    public void Priority_IsInSecurityRange()
    {
        new PolicyMutationTransformer().Priority.Should().BeInRange(0, 99);
    }

    // ---- Table action deny: create / update / delete ----

    [Theory]
    [InlineData(MutationType.Insert)]
    [InlineData(MutationType.Update)]
    [InlineData(MutationType.Delete)]
    public async Task Transform_ActionNotPermitted_ReturnsNonLeakingError(MutationType mutationType)
    {
        // Policy permits only read — create/update/delete are all denied.
        var model = ModelWithPolicy((MetadataKeys.Policy.Actions, "read"));
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["Total"] = 10m };

        var result = await transformer.TransformAsync(
            Orders(model), mutationType, data, Context(model, UserWithRoles("user")));

        result.Errors.Should().ContainSingle();
        result.Errors[0].Should().NotContain("Orders");
        result.Errors[0].Should().NotContain("Total");
        result.Errors[0].Should().NotContain(mutationType.ToString());
    }

    [Theory]
    [InlineData(MutationType.Insert, "create")]
    [InlineData(MutationType.Update, "update")]
    [InlineData(MutationType.Delete, "delete")]
    public async Task Transform_ActionPermitted_ReturnsNoErrors(MutationType mutationType, string action)
    {
        var model = ModelWithPolicy((MetadataKeys.Policy.Actions, action));
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["Total"] = 10m };

        var result = await transformer.TransformAsync(
            Orders(model), mutationType, data, Context(model, UserWithRoles("user")));

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_NoPolicyMetadata_AllowsByDefault()
    {
        // Absent-policy default established in sub-task 1 is ALLOW.
        var model = ModelWithPolicy();
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["Total"] = 10m };

        var result = await transformer.TransformAsync(
            Orders(model), MutationType.Insert, data, Context(model, UserWithRoles("user")));

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_AdminRole_ActionNotListed_IsDenied()
    {
        // D7: the admin bypass covers the GRANT requirement only. A policy that
        // lists actions is a product surface — an action it does not list is
        // denied for admins too, with the same generic ACCESS_DENIED refusal.
        var model = ModelWithPolicy((MetadataKeys.Policy.Actions, "read"));
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["Total"] = 10m };

        var result = await transformer.TransformAsync(
            Orders(model), MutationType.Delete, data, Context(model, UserWithRoles("admin")));

        result.Errors.Should().ContainSingle();
        result.Errors[0].Should().Be("Access denied by authorization policy.");
        result.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
    }

    [Fact]
    public async Task Transform_AdminRole_BypassesBracketGrantRequirement()
    {
        // D7 grant half: admin holds no grants yet passes a bracketed action.
        var model = ModelWithPolicy((MetadataKeys.Policy.Actions, "read,delete[projects.manage]"));
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        var result = await transformer.TransformAsync(
            Orders(model), MutationType.Delete, data, Context(model, UserWithRoles("admin")));

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_BracketGrant_CallerWithoutGrant_IsDenied()
    {
        var model = ModelWithPolicy((MetadataKeys.Policy.Actions, "read,delete[projects.manage]"));
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        var result = await transformer.TransformAsync(
            Orders(model), MutationType.Delete, data, Context(model, UserWithRoles("member")));

        result.Errors.Should().ContainSingle();
        result.Errors[0].Should().Be("Access denied by authorization policy.");
        result.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
    }

    [Fact]
    public async Task Transform_BracketGrant_CallerWithGrant_IsAllowed()
    {
        var model = ModelWithPolicy((MetadataKeys.Policy.Actions, "read,delete[projects.manage]"));
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        var result = await transformer.TransformAsync(
            Orders(model), MutationType.Delete, data,
            Context(model, UserWithRoles("member", "projects.manage")));

        result.Errors.Should().BeEmpty();
    }

    // ---- E13 / E6: policy runs before the soft-delete rewrite, so the soft
    //      form and the _hardDelete: true form meet the SAME decision. ----

    [Fact]
    public async Task Transform_PolicySeesOriginalDelete_BeforeSoftDeleteRewrite()
    {
        // E13: policy runs at priority 1, before soft-delete's DELETE→UPDATE
        // rewrite (priority 100). Granting update but not delete must still deny
        // a delete — if policy ran after the rewrite it would see Update and
        // allow. Both forms refuse with the generic ACCESS_DENIED code (E6).
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, "read,create,update"),
            (MetadataKeys.SoftDelete.Column, "deleted_at"),
            (MetadataKeys.SoftDelete.HardDeleteRole, "user"));
        var wrap = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new SoftDeleteMutationTransformer(),
                new PolicyMutationTransformer(),
            },
        };
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        var soft = await wrap.TransformAsync(
            Orders(model), MutationType.Delete, new(data), Context(model, UserWithRoles("user")));

        soft.Errors.Should().ContainSingle();
        soft.Errors[0].Should().Be("Access denied by authorization policy.");
        soft.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
    }

    [Fact]
    public async Task Transform_HardDeleteForm_MeetsTheSameRefusalAsTheSoftForm()
    {
        // E6, hard half: the _hardDelete: true form is refused by the SAME policy
        // decision (delete is not listed) with the SAME generic ACCESS_DENIED.
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, "read,create,update"),
            (MetadataKeys.SoftDelete.Column, "deleted_at"),
            (MetadataKeys.SoftDelete.HardDeleteRole, "user"));
        var wrap = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new SoftDeleteMutationTransformer(),
                new PolicyMutationTransformer(),
            },
        };
        var context = new MutationTransformContext
        {
            Model = model,
            UserContext = UserWithRoles("user"),
            ModuleArguments = new Dictionary<string, object?>
            {
                [SoftDeleteModuleApi.HardDeleteKey] = true,
            },
        };

        var hard = await wrap.TransformAsync(
            Orders(model), MutationType.Delete, new Dictionary<string, object?> { ["Id"] = 1 }, context);

        hard.Errors.Should().ContainSingle();
        hard.Errors[0].Should().Be("Access denied by authorization policy.");
        hard.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
    }

    // ---- Column write-deny ----

    [Fact]
    public async Task Transform_WritesDeniedColumn_ReturnsNonLeakingError()
    {
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, "update"),
            (MetadataKeys.Policy.WriteDeny, "ssn"));
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["Total"] = 10m, ["ssn"] = "123-45-6789" };

        var result = await transformer.TransformAsync(
            Orders(model), MutationType.Update, data, Context(model, UserWithRoles("user")));

        result.Errors.Should().ContainSingle();
        result.Errors[0].Should().NotContain("ssn");
        result.Errors[0].Should().NotContain("Orders");
    }

    [Fact]
    public async Task Transform_WritesOnlyAllowedColumns_ReturnsNoErrors()
    {
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, "update"),
            (MetadataKeys.Policy.WriteDeny, "ssn"));
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["Total"] = 10m, ["tenant_id"] = 1 };

        var result = await transformer.TransformAsync(
            Orders(model), MutationType.Update, data, Context(model, UserWithRoles("user")));

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_AdminRole_BypassesColumnWriteDeny()
    {
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, "update"),
            (MetadataKeys.Policy.WriteDeny, "ssn"));
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["ssn"] = "123-45-6789" };

        var result = await transformer.TransformAsync(
            Orders(model), MutationType.Update, data, Context(model, UserWithRoles("admin")));

        result.Errors.Should().BeEmpty();
    }

    // ---- Row-scope on update / delete ----

    [Theory]
    [InlineData(MutationType.Update, "update")]
    [InlineData(MutationType.Delete, "delete")]
    public async Task Transform_RowScope_CompilesToEqualityFilter(MutationType mutationType, string action)
    {
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, action),
            (MetadataKeys.Policy.RowScope, "tenant_id = {tenant_id}"));
        var transformer = new PolicyMutationTransformer();
        var userContext = UserWithRoles("user");
        userContext["tenant_id"] = 7;
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Total"] = 10m };

        var result = await transformer.TransformAsync(
            Orders(model), mutationType, data, Context(model, userContext));

        result.Errors.Should().BeEmpty();
        result.AdditionalFilter.Should().NotBeNull();
        result.AdditionalFilter!.ColumnName.Should().Be("tenant_id");
        result.AdditionalFilter.Next!.RelationName.Should().Be("_eq");
        result.AdditionalFilter.Next.Value.Should().Be(7);
    }

    [Fact]
    public async Task Transform_RowScopeOnCreate_NoAdditionalFilter()
    {
        // A create has no existing row to scope.
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, "create"),
            (MetadataKeys.Policy.RowScope, "tenant_id = {tenant_id}"));
        var transformer = new PolicyMutationTransformer();
        var userContext = UserWithRoles("user");
        userContext["tenant_id"] = 7;
        var data = new Dictionary<string, object?> { ["Total"] = 10m };

        var result = await transformer.TransformAsync(
            Orders(model), MutationType.Insert, data, Context(model, userContext));

        result.Errors.Should().BeEmpty();
        result.AdditionalFilter.Should().BeNull();
    }

    [Fact]
    public async Task Transform_AdminRole_SkipsRowScopeFilter()
    {
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, "update"),
            (MetadataKeys.Policy.RowScope, "tenant_id = {tenant_id}"));
        var transformer = new PolicyMutationTransformer();
        var userContext = UserWithRoles("admin");
        userContext["tenant_id"] = 7;
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Total"] = 10m };

        var result = await transformer.TransformAsync(
            Orders(model), MutationType.Update, data, Context(model, userContext));

        result.Errors.Should().BeEmpty();
        result.AdditionalFilter.Should().BeNull();
    }

    [Fact]
    public async Task Transform_RowScopeWithNoExpression_NoAdditionalFilter()
    {
        var model = ModelWithPolicy((MetadataKeys.Policy.Actions, "update"));
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Total"] = 10m };

        var result = await transformer.TransformAsync(
            Orders(model), MutationType.Update, data, Context(model, UserWithRoles("user")));

        result.AdditionalFilter.Should().BeNull();
    }

    // ---- Tenant-filter integration: the wrap ANDs the row-scope filter
    //      alongside a tenant-scoped transformer's filter rather than replacing it. ----

    [Fact]
    public async Task Integration_RowScopeIsAndedAlongsideAnotherTransformersFilter()
    {
        var model = ModelWithPolicy(
            (MetadataKeys.Policy.Actions, "update"),
            (MetadataKeys.Policy.RowScope, "tenant_id = {tenant_id}"));

        // A priority-0 transformer that contributes its own AdditionalFilter,
        // standing in for a tenant-scoped mutation transformer.
        var tenantLike = new StubFilterMutationTransformer(
            priority: 0,
            TableFilterFactory.Equals(model.GetTableFromDbName("Orders"), "Id", 1));

        var wrap = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                tenantLike,
                new PolicyMutationTransformer(),
            },
        };

        var userContext = UserWithRoles("user");
        userContext["tenant_id"] = 7;
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Total"] = 10m };

        var result = await wrap.TransformAsync(Orders(model), MutationType.Update, data, Context(model, userContext));

        result.Errors.Should().BeEmpty();
        result.AdditionalFilter.Should().NotBeNull();
        result.AdditionalFilter!.FilterType.Should().Be(FilterType.And);
        result.AdditionalFilter.And.Should().HaveCount(2);
        result.AdditionalFilter.And[0].ColumnName.Should().Be("Id");
        result.AdditionalFilter.And[1].ColumnName.Should().Be("tenant_id");
    }

    /// <summary>
    /// Minimal <see cref="IMutationTransformer"/> that always applies and
    /// contributes a fixed <see cref="MutationTransformResult.AdditionalFilter"/>.
    /// Stands in for a tenant-scoped transformer so the row-scope-AND-integration
    /// behavior can be asserted without the Server assembly.
    /// </summary>
    private sealed class StubFilterMutationTransformer : IMutationTransformer
    {
        private readonly TableFilter _filter;

        public StubFilterMutationTransformer(int priority, TableFilter filter)
        {
            Priority = priority;
            _filter = filter;
        }

        public int Priority { get; }

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context) => true;

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table,
            MutationType mutationType,
            Dictionary<string, object?> data,
            MutationTransformContext context) =>
            new(new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
                AdditionalFilter = _filter,
            });
    }
}
