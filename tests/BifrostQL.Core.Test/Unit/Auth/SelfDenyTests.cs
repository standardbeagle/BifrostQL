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
/// S6b <c>policy-self-deny</c>: an update that writes a listed column must not
/// match the caller's own row. The rule is expressed as a
/// <c>self-column _neq {user_id}</c> predicate ANDed into the statement (never a
/// pre-read), it is NOT admin-bypassable (D6), and a missing <c>user_id</c>
/// fails closed with the same generic refusal as the row-scope rule.
/// </summary>
public sealed class SelfDenyTests
{
    private const string ManageGrant = "profiles.manage";

    private static IDbModel Model(bool withRowScope = false, bool withWriteRequires = false, bool explicitSelfColumn = true) =>
        DbModelTestFixture.Create().WithTable("users", t =>
        {
            t.WithSchema("public").WithPrimaryKey("id")
                .WithColumn("user_id", "int")
                .WithColumn("permission_profile_id", "int")
                .WithColumn("cost_rate", "decimal")
                .WithColumn("display_name", "varchar")
                .WithMetadata(MetadataKeys.Policy.Actions, "read,create,update,delete")
                .WithMetadata(MetadataKeys.Policy.SelfDeny, "permission_profile_id, cost_rate");
            if (explicitSelfColumn)
                t.WithMetadata(MetadataKeys.Policy.SelfColumn, "id");
            if (withRowScope)
                t.WithMetadata(MetadataKeys.Policy.RowScope, "user_id = {user_id}");
            if (withWriteRequires)
                t.WithColumnMetadata("permission_profile_id", MetadataKeys.Policy.WriteRequires, ManageGrant);
        }).Build();

    private static MutationTransformContext Context(IDbModel model, string? userId, string role, params string[] grants)
    {
        var context = new Dictionary<string, object?> { ["roles"] = new[] { role } };
        if (userId is not null)
            context["user_id"] = userId;
        if (grants.Length > 0)
            context[MetadataKeys.Auth.DefaultPermissionsContextKey] = grants;
        return new MutationTransformContext { Model = model, UserContext = context };
    }

    private static Task<MutationTransformResult> Transform(
        IDbModel model, MutationType type, Dictionary<string, object?> data, MutationTransformContext context) =>
        new PolicyMutationTransformer().TransformAsync(model.GetTableFromDbName("users"), type, data, context).AsTask();

    private static void AssertSelfFilter(TableFilter? filter, string selfColumn, object userId)
    {
        filter.Should().NotBeNull();
        filter!.ColumnName.Should().Be(selfColumn);
        filter.Next!.RelationName.Should().Be(FilterOperators.Neq);
        filter.Next.Value.Should().Be(userId);
    }

    [Fact]
    public void Collector_ParsesSelfDenyColumnsAndSelfColumn()
    {
        var policy = PolicyConfigCollector.FromTable(Model().GetTableFromDbName("users"));

        policy.HasPolicy.Should().BeTrue();
        policy.SelfDenyColumns.Should().BeEquivalentTo("permission_profile_id", "cost_rate");
        policy.SelfColumn.Should().Be("id");
    }

    [Fact]
    public void Collector_MissingSelfColumn_DefaultsToUserIdByName()
    {
        var policy = PolicyConfigCollector.FromTable(Model(explicitSelfColumn: false).GetTableFromDbName("users"));

        policy.SelfColumn.Should().Be("user_id", "the default is the fixed user-id context key, not the audit claim");
        policy.SelfColumn.Should().Be(MetadataKeys.Auth.DefaultUserIdContextKey);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("member")]
    public async Task Update_TouchingSelfDenyColumn_FiltersOutOwnRow_ForEveryRole(string role)
    {
        // D6: the admin bypass is not consulted for this rule.
        var model = Model();
        var result = await Transform(model, MutationType.Update,
            new() { ["permission_profile_id"] = 2 }, Context(model, "42", role));

        result.Errors.Should().BeNullOrEmpty();
        AssertSelfFilter(result.AdditionalFilter, "id", 42);
    }

    [Fact]
    public async Task Update_TouchingOnlyUnlistedColumn_HasNoSelfFilter()
    {
        var model = Model();
        var result = await Transform(model, MutationType.Update,
            new() { ["display_name"] = "Renamed" }, Context(model, "42", "member"));

        result.Errors.Should().BeNullOrEmpty();
        result.AdditionalFilter.Should().BeNull();
    }

    [Theory]
    [InlineData(MutationType.Insert)]
    [InlineData(MutationType.Delete)]
    public async Task InsertAndDelete_HaveNoSelfFilter(MutationType type)
    {
        var model = Model();
        var result = await Transform(model, type,
            new() { ["permission_profile_id"] = 2 }, Context(model, "42", "admin"));

        result.Errors.Should().BeNullOrEmpty();
        result.AdditionalFilter.Should().BeNull();
    }

    [Fact]
    public async Task Update_WithoutUserIdInContext_IsRefusedAccessDenied()
    {
        // Fail closed, mirroring the row-scope missing-context refusal: same
        // generic message, same ACCESS_DENIED classification, no filter built.
        var model = Model();
        var result = await Transform(model, MutationType.Update,
            new() { ["permission_profile_id"] = 2 }, Context(model, userId: null, "admin"));

        result.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
        result.Errors.Should().ContainSingle().Which.Should().Be(RowScopeCompiler.MissingContextMessage);
        result.AdditionalFilter.Should().BeNull();
    }

    [Fact]
    public async Task Update_ComposesSelfFilterWithRowScope_ByAnd()
    {
        var model = Model(withRowScope: true);
        var result = await Transform(model, MutationType.Update,
            new() { ["cost_rate"] = 1.5m }, Context(model, "42", "member"));

        result.Errors.Should().BeNullOrEmpty();
        var filter = result.AdditionalFilter!;
        filter.FilterType.Should().Be(FilterType.And);
        filter.And.Should().HaveCount(2);
        AssertSelfFilter(filter.And[0], "id", 42);
        filter.And[1].ColumnName.Should().Be("user_id");
        filter.And[1].Next!.RelationName.Should().Be(FilterOperators.Eq);
        filter.And[1].Next!.Value.Should().Be(42);
    }

    [Fact]
    public async Task Update_AdminWithRowScope_KeepsSelfFilterOnly()
    {
        // The row scope honours the admin bypass; the self filter does not.
        var model = Model(withRowScope: true);
        var result = await Transform(model, MutationType.Update,
            new() { ["cost_rate"] = 1.5m }, Context(model, "42", "admin"));

        AssertSelfFilter(result.AdditionalFilter, "id", 42);
    }

    [Fact]
    public async Task Update_GrantOnSelfDenyColumn_StillFiltersOwnRow()
    {
        // A write grant admits the column; it never admits the caller's own row.
        var model = Model(withWriteRequires: true);
        var granted = await Transform(model, MutationType.Update,
            new() { ["permission_profile_id"] = 2 }, Context(model, "42", "member", ManageGrant));

        granted.Errors.Should().BeNullOrEmpty();
        AssertSelfFilter(granted.AdditionalFilter, "id", 42);

        // Grant absent: the existing column rule decides, unchanged by self deny.
        var ungranted = await Transform(model, MutationType.Update,
            new() { ["permission_profile_id"] = 2 }, Context(model, "42", "member"));
        ungranted.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
        ungranted.AdditionalFilter.Should().BeNull();
    }
}
