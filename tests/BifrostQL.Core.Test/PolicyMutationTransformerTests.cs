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
                    .WithColumn("Total", "decimal");
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
    public async Task Transform_AdminRole_BypassesActionDeny()
    {
        var model = ModelWithPolicy((MetadataKeys.Policy.Actions, "read"));
        var transformer = new PolicyMutationTransformer();
        var data = new Dictionary<string, object?> { ["Total"] = 10m };

        var result = await transformer.TransformAsync(
            Orders(model), MutationType.Delete, data, Context(model, UserWithRoles("admin")));

        result.Errors.Should().BeEmpty();
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
            TableFilterFactory.Equals("Orders", "Id", 1));

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
