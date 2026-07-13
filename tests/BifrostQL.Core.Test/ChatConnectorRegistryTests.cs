using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Chat;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for connector slice 2 — the <see cref="ChatConnectorRegistry"/> that turns
/// DI-registered <see cref="IChatConnector"/>s into the per-model
/// <see cref="ChatToolSet"/> the tool loop runs against. Pinned here: connectors are
/// offered every connector table in priority order, a duplicate tool name fails fast
/// naming both connectors and the tool, an invalid tool name never reaches the wire,
/// and execution dispatches by name with the caller's auth context threaded through —
/// no ambient identity anywhere in the chain.
/// </summary>
public class ChatConnectorRegistryTests
{
    private static IDbModel ConnectorModel(params string[] connectorTables)
    {
        var fixture = DbModelTestFixture.Create();
        foreach (var name in connectorTables.DefaultIfEmpty("documents"))
        {
            fixture.WithTable(name, t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore));
        }
        return fixture.Build();
    }

    /// <summary>Scripted connector: fixed tool names, recorded execute calls.</summary>
    private sealed class FakeConnector : IChatConnector
    {
        private readonly string[] _toolNames;

        public FakeConnector(int priority, params string[] toolNames)
        {
            Priority = priority;
            _toolNames = toolNames;
        }

        public int Priority { get; }

        public ChatToolResult Result { get; set; } = new() { TextPayload = """{"ok":true}""" };

        public List<(string ToolName, string InputJson, IDictionary<string, object?> AuthContext)> Executions { get; } = new();

        public IReadOnlyList<ChatToolDefinition> GetToolDefinitions(IDbModel model, ChatConnectorBinding binding)
            => _toolNames
                .Select(name => new ChatToolDefinition(
                    $"{name}_{binding.Table.DbName}",
                    $"Query the {binding.Table.DbName} table.",
                    """{"type":"object","properties":{}}"""))
                .ToList();

        public Task<ChatToolResult> ExecuteAsync(
            string toolName, string inputJson, IDictionary<string, object?> authContext, CancellationToken cancellationToken)
        {
            Executions.Add((toolName, inputJson, authContext));
            return Task.FromResult(Result);
        }
    }

    // ---- tool-set building ----

    [Fact]
    public void BuildToolSet_CollectsDefinitionsPerConnectorPerBinding_InPriorityOrder()
    {
        // Arrange: registration order is (200, 100); priority must win over it.
        var late = new FakeConnector(200, "plan");
        var early = new FakeConnector(100, "explore");
        var registry = new ChatConnectorRegistry(new IChatConnector[] { late, early });

        // Act
        var tools = registry.BuildToolSet(ConnectorModel("documents", "orders"));

        // Assert: lower priority first, bindings in table order within a connector.
        tools.IsEmpty.Should().BeFalse();
        tools.Definitions.Select(d => d.Name).Should().Equal(
            "explore_documents", "explore_orders", "plan_documents", "plan_orders");
        tools.Definitions[0].Description.Should().Be("Query the documents table.");
        tools.Definitions[0].InputSchemaJson.Should().Contain("\"type\":\"object\"");
    }

    [Fact]
    public void BuildToolSet_NoConnectorTables_IsEmpty()
    {
        var registry = new ChatConnectorRegistry(new IChatConnector[] { new FakeConnector(100, "explore") });
        var model = DbModelTestFixture.Create()
            .WithTable("plain", t => t.WithSchema("dbo").WithPrimaryKey("Id"))
            .Build();

        var tools = registry.BuildToolSet(model);

        tools.IsEmpty.Should().BeTrue();
        tools.Definitions.Should().BeEmpty();
    }

    [Fact]
    public void BuildToolSet_NoRegisteredConnectors_IsEmpty()
    {
        var registry = new ChatConnectorRegistry(Array.Empty<IChatConnector>());

        registry.BuildToolSet(ConnectorModel()).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void BuildToolSet_DuplicateToolNameAcrossConnectors_FailsFast_NamingBothConnectorsAndTool()
    {
        // Arrange: two connectors both expose 'explore_documents'; a silent
        // last-one-wins would execute the wrong connector's tool.
        var first = new FakeConnector(100, "explore");
        var second = new FakeConnector(150, "explore");
        var registry = new ChatConnectorRegistry(new IChatConnector[] { first, second });

        // Act
        var act = () => registry.BuildToolSet(ConnectorModel());

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*explore_documents*")
            .WithMessage("*FakeConnector*")
            .WithMessage("*unique*");
    }

    [Fact]
    public void BuildToolSet_InvalidToolName_FailsFast()
    {
        // Arrange: 'explore documents' has a space — Claude rejects it; fail before
        // the wire does.
        var connector = new FakeConnector(100, "explore documents");
        var registry = new ChatConnectorRegistry(new IChatConnector[] { connector });

        var act = () => registry.BuildToolSet(ConnectorModel());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*explore documents*")
            .WithMessage("*FakeConnector*");
    }

    // ---- execution dispatch ----

    [Fact]
    public async Task ExecuteAsync_DispatchesByName_ThreadingAuthContextAndInput()
    {
        var explore = new FakeConnector(100, "explore");
        var plan = new FakeConnector(150, "plan");
        var tools = new ChatConnectorRegistry(new IChatConnector[] { explore, plan })
            .BuildToolSet(ConnectorModel());
        var authContext = new Dictionary<string, object?> { ["sub"] = "user-1" };

        var result = await tools.ExecuteAsync(
            "plan_documents", """{"op":"insert"}""", authContext, CancellationToken.None);

        result.TextPayload.Should().Be("""{"ok":true}""");
        explore.Executions.Should().BeEmpty();
        plan.Executions.Should().ContainSingle();
        plan.Executions[0].ToolName.Should().Be("plan_documents");
        plan.Executions[0].InputJson.Should().Be("""{"op":"insert"}""");
        plan.Executions[0].AuthContext.Should().BeSameAs(authContext);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_ThrowsTheModelVisibleInputException()
    {
        // ChatToolInputException so the tool loop feeds the (authored, secret-free)
        // message back to the model verbatim instead of a sanitized type name.
        var tools = new ChatConnectorRegistry(new IChatConnector[] { new FakeConnector(100, "explore") })
            .BuildToolSet(ConnectorModel());

        var act = () => tools.ExecuteAsync(
            "not_a_tool", "{}", new Dictionary<string, object?>(), CancellationToken.None);

        await act.Should().ThrowAsync<ChatToolInputException>().WithMessage("*not_a_tool*");
    }

    [Fact]
    public async Task CreateExecutor_BindsAuthContextIntoEveryCall()
    {
        // The completion layer only ever sees the executor — identity must already be
        // inside it, bound per request, never ambient.
        var connector = new FakeConnector(100, "explore");
        var tools = new ChatConnectorRegistry(new IChatConnector[] { connector })
            .BuildToolSet(ConnectorModel());
        var authContext = new Dictionary<string, object?> { ["tenant"] = "tenant-a" };

        var executor = tools.CreateExecutor(authContext);
        await executor.ExecuteAsync("explore_documents", "{}", CancellationToken.None);

        connector.Executions.Should().ContainSingle()
            .Which.AuthContext.Should().BeSameAs(authContext);
    }
}
