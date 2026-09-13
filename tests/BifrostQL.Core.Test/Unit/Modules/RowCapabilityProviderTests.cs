using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Model;
using FluentAssertions;
using Xunit;
using NSubstitute;

namespace BifrostQL.Core.Test.Unit.Modules;

public sealed class RowCapabilityProviderTests
{
    [Fact]
    public void Collector_EmitsCanOnlyForRowScopedTable()
    {
        var table = TestTable("policy-row-scope: user_id = {user_id}; policy-actions: update, delete");

        var definition = ComputedColumnConfigCollector.FromTable(table)
            .Single(c => c.Name == RowCapabilityProvider.FieldName);

        definition.GraphQlType.Should().Be("RowCapabilities!");
        definition.Dependencies.Should().Equal("user_id");
        definition.ExpressionOrProvider.Should().Be(RowCapabilityProvider.ProviderName);
        ComputedColumnConfigCollector.FromTable(TestTable("policy-actions: update"))
            .Should().NotContain(c => c.Name == RowCapabilityProvider.FieldName);
    }

    [Fact]
    public async Task Provider_ComposesScopeAndActionAndFailsClosedWithoutUser()
    {
        var table = TestTable("policy-row-scope: user_id = {user_id}; policy-actions: update, delete; policy-row-scope-exempt: time.edit_others");
        var provider = new RowCapabilityProvider();

        var own = await Compute(provider, table, Row(user: 1), Caller(userId: 1));
        var colleague = await Compute(provider, table, Row(user: 2), Caller(userId: 1));
        var exempt = await Compute(provider, table, Row(user: 2), Caller(userId: 1, grants: "time.edit_others"));
        var missing = await Compute(provider, table, Row(user: 2), Caller(userId: null));

        own.Update.Should().BeTrue();
        colleague.Update.Should().BeFalse();
        exempt.Update.Should().BeTrue();
        missing.Update.Should().BeFalse();
    }

    [Fact]
    public void Collector_DependsOnTheScopeColumnNamedByTheExpression()
    {
        var table = TestTable("policy-row-scope: household_id = {household_id}; policy-actions: update, delete");

        ComputedColumnConfigCollector.FromTable(table)
            .Single(c => c.Name == RowCapabilityProvider.FieldName)
            .Dependencies.Should().Equal("household_id");
    }

    [Fact]
    public async Task Provider_ResolvesTheContextKeyNamedByTheExpression_NotTheUserId()
    {
        // household_id = {household_id}: the row's household is compared with the
        // caller's household_id context value, never with the caller's user id. The
        // context value arrives as an int while the row value is the reader's long,
        // so the comparison must coerce like the transformers do, not ToString.
        var table = TestTable("policy-row-scope: household_id = {household_id}; policy-actions: update, delete");
        var provider = new RowCapabilityProvider();

        var own = await Compute(provider, table, Row(user: 42, household: 7L), Caller(userId: 42, householdId: 7));
        var other = await Compute(provider, table, Row(user: 42, household: 8L), Caller(userId: 42, householdId: 7));
        var missing = await Compute(provider, table, Row(user: 42, household: 7L), Caller(userId: 42));

        own.Update.Should().BeTrue();
        own.Delete.Should().BeTrue();
        other.Update.Should().BeFalse();
        missing.Update.Should().BeFalse("a missing context value fails closed like RowScopeCompiler.Compile");
    }

    [Fact]
    public async Task Provider_ReadsTheScopeColumnByGraphQlNameWhenTheRowIsKeyedThatWay()
    {
        var table = TestTable("policy-row-scope: user_id = {user_id}; policy-actions: update");
        var provider = new RowCapabilityProvider();

        var own = await Compute(provider, table, new Dictionary<string, object?> { ["userId"] = 1L }, Caller(userId: 1));

        own.Update.Should().BeTrue();
    }

    [Fact]
    public void Collector_EmitsCanForSelfDenyTable_DependingOnTheSelfColumn()
    {
        var selfDenyOnly = ComputedColumnConfigCollector.FromTable(
                TestTable("policy-actions: update; policy-self-deny: profile_id; policy-self-column: user_id"))
            .Single(c => c.Name == RowCapabilityProvider.FieldName);
        selfDenyOnly.Dependencies.Should().Equal("user_id");

        var both = ComputedColumnConfigCollector.FromTable(
                TestTable("policy-row-scope: household_id = {household_id}; policy-actions: update; policy-self-deny: profile_id; policy-self-column: user_id"))
            .Single(c => c.Name == RowCapabilityProvider.FieldName);
        both.Dependencies.Should().BeEquivalentTo("household_id", "user_id");
    }

    [Fact]
    public async Task Provider_SelfDenyRefusesUpdateOnTheCallersOwnRow_AdminIncluded()
    {
        // policy-self-column defaults to user_id when omitted (PolicyConfigCollector).
        var table = TestTable("policy-actions: update, delete; policy-self-deny: profile_id");
        var provider = new RowCapabilityProvider();

        var own = await Compute(provider, table, Row(user: 1, profile: 5), Caller(userId: 1));
        var colleague = await Compute(provider, table, Row(user: 2, profile: 5), Caller(userId: 1));
        var adminOwn = await Compute(provider, table, Row(user: 1, profile: 5), Admin(userId: 1));
        var missing = await Compute(provider, table, Row(user: 2, profile: 5), Caller(userId: null));

        own.Update.Should().BeFalse("self-deny excludes the caller's own row from update");
        own.Delete.Should().BeTrue("self-deny narrows update only");
        colleague.Update.Should().BeTrue();
        adminOwn.Update.Should().BeFalse("the admin bypass is deliberately not consulted for self-deny");
        missing.Update.Should().BeFalse("a missing user id fails closed like PolicyMutationTransformer");
    }

    [Fact]
    public async Task Provider_SelfDenyComposesWithRowScope()
    {
        var table = TestTable("policy-row-scope: user_id = {user_id}; policy-actions: update, delete; policy-self-deny: profile_id");
        var provider = new RowCapabilityProvider();

        var own = await Compute(provider, table, Row(user: 1, profile: 5), Caller(userId: 1));
        var colleague = await Compute(provider, table, Row(user: 2, profile: 5), Caller(userId: 1));

        own.Should().Be(new RowCapabilities(Update: false, Delete: true));
        colleague.Should().Be(new RowCapabilities(Update: false, Delete: false));
    }

    private static async Task<RowCapabilities> Compute(
        RowCapabilityProvider provider,
        IDbTable table,
        IReadOnlyDictionary<string, object?> row,
        IDictionary<string, object?> userContext)
    {
        var definition = ComputedColumnConfigCollector.FromTable(table).Single(c => c.Name == RowCapabilityProvider.FieldName);
        var result = await provider.ComputeAsync(new ComputedColumnContext
        {
            Model = Substitute.For<IDbModel>(),
            Table = table,
            Column = definition,
            Row = row,
            UserContext = userContext,
        });
        return (RowCapabilities)result!;
    }

    private static IReadOnlyDictionary<string, object?> Row(long user, long? household = null, long? profile = null)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["user_id"] = user };
        if (household is not null) row["household_id"] = household;
        if (profile is not null) row["profile_id"] = profile;
        return row;
    }

    private static IDictionary<string, object?> Caller(int? userId, int? householdId = null, params string[] grants)
    {
        var context = new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultUserIdContextKey] = userId,
            [MetadataKeys.Auth.DefaultPermissionsContextKey] = grants,
        };
        if (householdId is not null) context["household_id"] = householdId;
        return context;
    }

    private static IDictionary<string, object?> Admin(int userId)
        => new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultUserIdContextKey] = userId,
            [MetadataKeys.Auth.DefaultRolesContextKey] = new[] { MetadataKeys.Policy.DefaultAdminRole },
        };

    /// <summary>
    /// A table with int columns user_id / household_id / profile_id whose policy
    /// metadata is exactly the <c>key: value; key: value</c> string given, so each
    /// fact declares the whole policy it runs against.
    /// </summary>
    private static IDbTable TestTable(string metadata)
    {
        var values = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Split(':', 2, StringSplitOptions.TrimEntries))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        var columns = new[]
        {
            new ColumnDto { ColumnName = "user_id", GraphQlName = "userId", DataType = "int" },
            new ColumnDto { ColumnName = "household_id", GraphQlName = "householdId", DataType = "int" },
            new ColumnDto { ColumnName = "profile_id", GraphQlName = "profileId", DataType = "int" },
        };
        var table = Substitute.For<IDbTable>();
        table.Columns.Returns(columns);
        table.ColumnLookup.Returns(columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase));
        table.GraphQlLookup.Returns(columns.ToDictionary(c => c.GraphQlName, StringComparer.OrdinalIgnoreCase));
        table.GetMetadataValue(Arg.Any<string>()).Returns(call => values.GetValueOrDefault(call.Arg<string>()));
        return table;
    }
}
