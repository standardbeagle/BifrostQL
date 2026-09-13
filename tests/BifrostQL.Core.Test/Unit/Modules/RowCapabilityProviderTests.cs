using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
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

        definition.GraphQlType.Should().Be("{ update: Boolean!, delete: Boolean! }");
        definition.Dependencies.Should().Equal("user_id");
        definition.ExpressionOrProvider.Should().Be(RowCapabilityProvider.ProviderName);
        ComputedColumnConfigCollector.FromTable(TestTable("policy-actions: update"))
            .Should().NotContain(c => c.Name == RowCapabilityProvider.FieldName);
    }

    [Fact]
    public async Task Provider_ComposesScopeAndActionAndFailsClosedWithoutUser()
    {
        var table = TestTable("policy-row-scope: user_id = {user_id}; policy-actions: update, delete; policy-row-scope-exempt: time.edit_others");
        var definition = ComputedColumnConfigCollector.FromTable(table).Single(c => c.Name == RowCapabilityProvider.FieldName);
        var model = Substitute.For<IDbModel>();
        var provider = new RowCapabilityProvider();

        var own = await provider.ComputeAsync(Context(model, table, definition, "member", "member"));
        var colleague = await provider.ComputeAsync(Context(model, table, definition, "member", "colleague"));
        var exempt = await provider.ComputeAsync(Context(model, table, definition, "member", "colleague", "time.edit_others"));
        var missing = await provider.ComputeAsync(Context(model, table, definition, null, "colleague"));

        ((bool)((IDictionary<string, object?>)own!)["update"]).Should().BeTrue();
        ((bool)((IDictionary<string, object?>)colleague!)["update"]).Should().BeFalse();
        ((bool)((IDictionary<string, object?>)exempt!)["update"]).Should().BeTrue();
        ((bool)((IDictionary<string, object?>)missing!)["update"]).Should().BeFalse();
    }

    private static ComputedColumnContext Context(IDbModel model, IDbTable table, ComputedColumnDefinition definition, string? userId, string rowUser, params string[] grants)
        => new()
        {
            Model = model,
            Table = table,
            Column = definition,
            Row = new Dictionary<string, object?> { ["user_id"] = rowUser },
            UserContext = new Dictionary<string, object?>
            {
                [MetadataKeys.Auth.DefaultUserIdContextKey] = userId,
                [MetadataKeys.Auth.DefaultPermissionsContextKey] = grants,
            },
        };

    private static IDbTable TestTable(string metadata)
    {
        var table = Substitute.For<IDbTable>();
        var user = Substitute.For<IDbColumn>();
        user.DbName.Returns("user_id");
        user.GraphQlName.Returns("userId");
        table.Columns.Returns(new[] { user });
        table.ColumnLookup.Returns(new Dictionary<string, IDbColumn>(StringComparer.OrdinalIgnoreCase) { ["user_id"] = user });
        table.GraphQlLookup.Returns(new Dictionary<string, IDbColumn>(StringComparer.OrdinalIgnoreCase) { ["userId"] = user });
        table.GetMetadataValue(Arg.Any<string>()).Returns(call => call.Arg<string>() switch
        {
            MetadataKeys.Policy.RowScope => metadata.Contains("policy-row-scope:") ? "user_id = {user_id}" : null,
            MetadataKeys.Policy.Actions => metadata.Contains("policy-actions:") ? "update, delete" : null,
            MetadataKeys.Policy.RowScopeExempt => metadata.Contains("exempt:") ? "time.edit_others" : null,
            _ => null,
        });
        return table;
    }
}
