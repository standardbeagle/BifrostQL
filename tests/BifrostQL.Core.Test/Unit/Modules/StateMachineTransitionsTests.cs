using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

public sealed class StateMachineTransitionsTests
{
    private const string StateColumn = "status";

    private static IDbModel BuildModel() =>
        DbModelTestFixture.Create()
            .WithTable("Members", t => t
                .WithPrimaryKey("Id")
                .WithColumn(StateColumn, "varchar")
                .WithMetadata(MetadataKeys.StateMachine.StateColumn, StateColumn)
                .WithMetadata(MetadataKeys.StateMachine.InitialState, "pending")
                .WithMetadata(MetadataKeys.StateMachine.States, "pending, active, inactive")
                .WithMetadata(MetadataKeys.StateMachine.Transitions,
                    "pending->active[officer]; active->inactive; inactive->active[officer]"))
            .WithTable("Notes", t => t
                .WithPrimaryKey("Id")
                .WithColumn("body", "varchar"))
            .Build();

    private static ComputedColumnContext Context(IDbModel model, string currentState, params string[] roles) =>
        new()
        {
            Model = model,
            Table = model.GetTableFromDbName("Members"),
            Column = ComputedColumnConfigCollector.Find(model.GetTableFromDbName("Members"), StateMachineTransitionsProvider.FieldName)!,
            Row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [StateColumn] = currentState },
            UserContext = new Dictionary<string, object?>
            {
                [MetadataKeys.Auth.DefaultUserIdContextKey] = "user-1",
                [MetadataKeys.Auth.DefaultRolesContextKey] = roles,
            },
        };

    [Fact]
    public void Collector_EmitsAvailableTransitions_OnStateMachineTable()
    {
        var model = BuildModel();
        var columns = ComputedColumnConfigCollector.FromTable(model.GetTableFromDbName("Members"));

        var field = columns.Should().ContainSingle(c => c.Name == StateMachineTransitionsProvider.FieldName).Subject;
        field.Kind.Should().Be(ComputedColumnKind.Provider);
        field.GraphQlType.Should().Be("[String!]");
        field.ExpressionOrProvider.Should().Be(StateMachineTransitionsProvider.ProviderName);
        field.Dependencies.Should().BeEquivalentTo(StateColumn);
    }

    [Fact]
    public void Collector_DoesNotEmit_OnTableWithoutStateMachineMetadata()
    {
        var model = BuildModel();
        var columns = ComputedColumnConfigCollector.FromTable(model.GetTableFromDbName("Notes"));

        columns.Should().NotContain(c => c.Name == StateMachineTransitionsProvider.FieldName);
    }

    [Fact]
    public void Schema_EmitsField_OnlyOnStateMachineTable()
    {
        var model = BuildModel();

        var memberSdl = new TableSchemaGenerator(model.GetTableFromDbName("Members"))
            .GetTableTypeDefinition(model, includeDynamicJoins: false);
        var noteSdl = new TableSchemaGenerator(model.GetTableFromDbName("Notes"))
            .GetTableTypeDefinition(model, includeDynamicJoins: false);

        memberSdl.Should().Contain("_availableTransitions : [String!]");
        noteSdl.Should().NotContain("_availableTransitions");
    }

    [Fact]
    public void Provider_ProjectsStateColumnAsOnlyDependency()
    {
        var model = BuildModel();
        var table = model.GetTableFromDbName("Members");
        var computed = ComputedColumnConfigCollector.Find(table, StateMachineTransitionsProvider.FieldName)!;
        var query = new GqlObjectQuery
        {
            DbTable = table,
            TableName = table.DbName,
            SchemaName = table.TableSchema,
            GraphQlName = table.GraphQlName,
            ScalarColumns = new List<GqlObjectColumn> { new(computed, computed.Name) },
        };

        query.FullColumnNames.Select(c => c.DbDbName).Should().BeEquivalentTo(StateColumn);
    }

    [Fact]
    public async Task Provider_ReturnsRoleGatedTransition_ForCallerWithRole()
    {
        var model = BuildModel();
        var provider = new StateMachineTransitionsProvider();

        var result = await provider.ComputeAsync(Context(model, "pending", "officer"));

        result.Should().BeEquivalentTo(new[] { "active" });
    }

    [Fact]
    public async Task Provider_OmitsRoleGatedTransition_ForCallerWithoutRole()
    {
        var model = BuildModel();
        var provider = new StateMachineTransitionsProvider();

        var result = await provider.ComputeAsync(Context(model, "pending"));

        result.Should().BeAssignableTo<IEnumerable<string>>()
            .Which.Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_ReturnsNull_WhenRowHasNoState()
    {
        // Null (not empty) lets clients distinguish "row has no state" from
        // "no transitions permitted from the current state" (empty list).
        var model = BuildModel();
        var provider = new StateMachineTransitionsProvider();

        var result = await provider.ComputeAsync(Context(model, currentState: ""));

        result.Should().BeNull();
    }

    [Fact]
    public async Task Provider_AllowsAdminThroughRoleGate()
    {
        var model = BuildModel();
        var provider = new StateMachineTransitionsProvider();

        var result = await provider.ComputeAsync(Context(model, "pending", "admin"));

        result.Should().BeEquivalentTo(new[] { "active" });
    }

    [Fact]
    public async Task Provider_ReturnsOpenTransition_ReflectingCurrentState()
    {
        var model = BuildModel();
        var provider = new StateMachineTransitionsProvider();

        // From "active" the only transition (active->inactive) requires no role.
        var result = await provider.ComputeAsync(Context(model, "active"));

        result.Should().BeEquivalentTo(new[] { "inactive" });
    }
}
