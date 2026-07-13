using Anthropic.Models.Messages;
using Anthropic.Services;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Chat;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// End-to-end proof of the explore chat connector (connector slice 3): one
/// schema-derived read tool per <c>chat-connector: explore</c> table, executed
/// through <see cref="IQueryIntentExecutor"/> under the caller's auth context so
/// tenant isolation and crypto masking hold BY CONSTRUCTION — the connector has no
/// SQL of its own. Pinned here: the generated tool definition (name, prescriptive
/// description incorporating <c>chat-tool-description</c>, filter/sort/projection
/// input schema), tenant-scoped execution fail-closed in both directions, the
/// row/payload caps with their explicit never-silent notes, encrypted-column
/// projection through the intent-read seam, model-visible input validation, the
/// full tool loop over SSE fixtures, and the default server registration.
/// </summary>
public sealed class ExploreChatConnectorTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_explore_connector_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private const string KeyRef = "config:orders";
    private const string UnmaskRole = "finance";
    private const string CardPlaintext = "4111-1111-1111-1111";

    private SqliteConnection _keepAlive = null!;
    private EnvelopeKeyManager _keyManager = null!;
    private ServiceProvider _provider = null!;
    private IQueryIntentExecutor _reads = null!;
    private IMutationIntentExecutor _writes = null!;
    private ExploreChatConnector _connector = null!;

    private static readonly string[] Rules =
    {
        "main.orders { chat-connector: explore; " +
            "chat-tool-description: One row per customer order, totals are in USD.; " +
            "tenant-filter: tenant_id }",
        "main.orders.card_number { encrypt: aes-256-gcm; key-ref: config:orders; mask: redact; unmask-role: finance }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS orders");
        await Exec(
            """
            CREATE TABLE orders (
                id          INTEGER PRIMARY KEY,
                tenant_id   INTEGER NOT NULL,
                customer    TEXT NULL,
                total       REAL NULL,
                created_at  DATETIME NULL,
                card_number TEXT NULL
            )
            """);

        var root = new byte[FieldCipher.KeySize];
        for (var i = 0; i < root.Length; i++) root[i] = (byte)(i + 11);
        _keyManager = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), new InMemoryDataEncryptionKeyStore());

        var services = new ServiceCollection();
        services.AddSingleton(_keyManager);
        _provider = services.BuildServiceProvider();

        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["dbSchema"] = DbSchema.FromModel(model),
                ["connFactory"] = factory,
            });
        });

        _reads = new QueryIntentExecutor(
            pathCache,
            new QueryTransformerService(new FilterTransformersWrap
            {
                Transformers = new IFilterTransformer[]
                {
                    new TenantFilterTransformer(),
                    new EncryptedColumnReadGuard(),
                },
            }),
            observers: null,
            services: _provider);

        _writes = new MutationIntentExecutor(
            pathCache,
            new MutationTransformersWrap
            {
                Transformers = new IMutationTransformer[]
                {
                    new TenantMutationTransformer(),
                    new EncryptOnWriteMutationTransformer(),
                },
            },
            _provider);

        _connector = new ExploreChatConnector(_reads, endpoint: EndpointPath);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private static IDictionary<string, object?> Tenant(int tenantId, params string[] roles) =>
        new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId,
            ["roles"] = roles,
        };

    /// <summary>Seeds an order through the mutation pipeline so tenant pinning and encrypt-on-write apply.</summary>
    private async Task SeedOrderAsync(int tenantId, string customer, double total, string? cardNumber = null)
    {
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["customer"] = customer,
            ["total"] = total,
            ["created_at"] = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
        };
        if (cardNumber != null)
            data["card_number"] = cardNumber;
        await _writes.ExecuteAsync(new MutationIntent
        {
            Table = "orders",
            Action = MutationIntentAction.Insert,
            Data = data,
            UserContext = Tenant(tenantId),
            Endpoint = EndpointPath,
        });
    }

    private async Task<JsonDocument> ExecuteAsync(
        string inputJson, IDictionary<string, object?> authContext, ExploreChatConnector? connector = null)
    {
        var result = await (connector ?? _connector)
            .ExecuteAsync("explore_orders", inputJson, authContext, CancellationToken.None);
        result.IsError.Should().BeFalse();
        return JsonDocument.Parse(result.TextPayload);
    }

    private static List<string?> Customers(JsonDocument payload) =>
        payload.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("customer").GetString()).ToList();

    private static string? Note(JsonDocument payload) =>
        payload.RootElement.TryGetProperty("note", out var note) ? note.GetString() : null;

    // ---- tool definitions ------------------------------------------------------

    [Fact]
    public async Task ToolDefinitions_PinNameDescriptionAndInputSchema()
    {
        var model = await _reads.GetModelAsync(EndpointPath);
        var binding = ChatConnectorConfig.FromModel(model).Should().ContainSingle().Subject;

        var definition = _connector.GetToolDefinitions(model, binding).Should().ContainSingle().Subject;

        definition.Name.Should().Be("explore_orders");
        definition.Description.Should().StartWith(
            "Call this when the user asks about orders (table main.orders).");
        definition.Description.Should().Contain("read-only");
        definition.Description.Should().EndWith(
            "One row per customer order, totals are in USD.",
            "the chat-tool-description metadata steers the model");

        using var schema = JsonDocument.Parse(definition.InputSchemaJson);
        var schemaRoot = schema.RootElement;
        schemaRoot.GetProperty("type").GetString().Should().Be("object");
        schemaRoot.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();

        var properties = schemaRoot.GetProperty("properties");
        properties.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "filters", "sort", "limit", "offset", "columns");

        // filters: one entry per column, operator set derived from the column type.
        var filters = properties.GetProperty("filters");
        filters.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        var filterColumns = filters.GetProperty("properties");
        filterColumns.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "tenant_id", "customer", "total", "created_at", "card_number");

        static List<string> Operators(JsonElement column) =>
            column.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

        Operators(filterColumns.GetProperty("customer")).Should().BeEquivalentTo("_eq", "_contains");
        Operators(filterColumns.GetProperty("total")).Should().BeEquivalentTo(
            "_eq", "_gt", "_gte", "_lt", "_lte", "_between");
        Operators(filterColumns.GetProperty("created_at")).Should().BeEquivalentTo(
            "_eq", "_gt", "_gte", "_lt", "_lte", "_between");
        filterColumns.GetProperty("customer").GetProperty("additionalProperties").GetBoolean().Should().BeFalse();

        // _between is a two-element array of the column's scalar type.
        var between = filterColumns.GetProperty("total").GetProperty("properties").GetProperty("_between");
        between.GetProperty("type").GetString().Should().Be("array");
        between.GetProperty("minItems").GetInt32().Should().Be(2);
        between.GetProperty("maxItems").GetInt32().Should().Be(2);
        between.GetProperty("items").GetProperty("type").GetString().Should().Be("number");

        // sort: schema-derived column enum + asc/desc.
        var sort = properties.GetProperty("sort");
        sort.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        sort.GetProperty("properties").GetProperty("column").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(
                "id", "tenant_id", "customer", "total", "created_at", "card_number");
        sort.GetProperty("properties").GetProperty("direction").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("asc", "desc");

        properties.GetProperty("limit").GetProperty("minimum").GetInt32().Should().Be(1);
        properties.GetProperty("offset").GetProperty("minimum").GetInt32().Should().Be(0);
        properties.GetProperty("columns").GetProperty("items").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(
                "id", "tenant_id", "customer", "total", "created_at", "card_number");
    }

    [Fact]
    public void ToolDefinitions_NonExploreBinding_YieldsNoTool()
    {
        // A plan-only connector table is another slice's tool; the explore connector
        // must return empty, not a read tool over a write-gated table.
        var model = DbModelTestFixture.Create()
            .WithTable("approvals", t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypePlan)
                .WithMetadata(MetadataKeys.ChatConnector.PlanOperations, "update"))
            .Build();
        var binding = ChatConnectorConfig.FromModel(model).Should().ContainSingle().Subject;

        _connector.GetToolDefinitions(model, binding).Should().BeEmpty();
    }

    // ---- tenant-scoped execution ------------------------------------------------

    [Fact]
    public async Task Execute_ReturnsOnlyTheCallerTenantsRows()
    {
        await SeedOrderAsync(1, "alice", 10.5);
        await SeedOrderAsync(1, "bob", 20.0);
        await SeedOrderAsync(2, "mallory", 99.0);

        using var tenantA = await ExecuteAsync("{}", Tenant(1));
        using var tenantB = await ExecuteAsync("{}", Tenant(2));

        Customers(tenantA).Should().Equal("alice", "bob");
        Customers(tenantB).Should().Equal("mallory");
        Note(tenantA).Should().BeNull("nothing was trimmed");
    }

    [Fact]
    public async Task Execute_ExplicitCrossTenantFilter_ReturnsZeroRows_FailClosedBothDirections()
    {
        await SeedOrderAsync(1, "alice", 10.5);
        await SeedOrderAsync(2, "mallory", 99.0);

        // The model asks for the OTHER tenant's rows by name: the tenant filter
        // transformer still pins the caller's tenant, so the conjunction is empty.
        using var fromTenant1 = await ExecuteAsync("""{"filters":{"tenant_id":{"_eq":2}}}""", Tenant(1));
        using var fromTenant2 = await ExecuteAsync("""{"filters":{"tenant_id":{"_eq":1}}}""", Tenant(2));

        fromTenant1.RootElement.GetProperty("rows").GetArrayLength().Should().Be(0);
        fromTenant2.RootElement.GetProperty("rows").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Execute_FiltersSortLimitOffsetAndProjection_ShapeTheQuery()
    {
        await SeedOrderAsync(1, "alice", 10.0);
        await SeedOrderAsync(1, "bob", 30.0);
        await SeedOrderAsync(1, "carol", 20.0);
        await SeedOrderAsync(1, "dave", 5.0);

        using var payload = await ExecuteAsync(
            """
            {"filters":{"total":{"_gte":10}},
             "sort":{"column":"total","direction":"desc"},
             "limit":2,"offset":1,
             "columns":["customer","total"]}
            """, Tenant(1));

        var rows = payload.RootElement.GetProperty("rows").EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("customer").GetString()).Should().Equal("carol", "alice");
        rows[0].EnumerateObject().Select(p => p.Name).Should().Equal("customer", "total");
    }

    // ---- caps: never silent -------------------------------------------------------

    [Fact]
    public async Task Execute_RowCapTrims_ReportsTheOmissionExplicitly()
    {
        for (var i = 1; i <= 8; i++)
            await SeedOrderAsync(1, $"customer-{i}", i);
        var capped = new ExploreChatConnector(
            _reads, new ChatConnectorOptions { ExploreRowCap = 5 }, EndpointPath);

        using var payload = await ExecuteAsync("{}", Tenant(1), capped);

        payload.RootElement.GetProperty("rows").GetArrayLength().Should().Be(5);
        Note(payload).Should().Be("showing 5 of at least 6 rows; narrow with filters");
    }

    [Fact]
    public async Task Execute_ModelSuppliedLimitBelowTheCap_StillReportsMoreRows()
    {
        for (var i = 1; i <= 5; i++)
            await SeedOrderAsync(1, $"customer-{i}", i);

        using var payload = await ExecuteAsync("""{"limit":3}""", Tenant(1));

        payload.RootElement.GetProperty("rows").GetArrayLength().Should().Be(3);
        Note(payload).Should().Be("showing 3 of at least 4 rows; narrow with filters");
    }

    [Fact]
    public async Task Execute_PayloadCapTrims_ReportsTheOmittedCountExplicitly()
    {
        for (var i = 1; i <= 5; i++)
            await SeedOrderAsync(1, $"customer-{i}-{new string('x', 80)}", i);
        var capped = new ExploreChatConnector(
            _reads, new ChatConnectorOptions { ExplorePayloadCharCap = 350 }, EndpointPath);

        using var payload = await ExecuteAsync("""{"columns":["customer"]}""", Tenant(1), capped);

        var shown = payload.RootElement.GetProperty("rows").GetArrayLength();
        shown.Should().BeGreaterThan(0).And.BeLessThan(5);
        var omitted = 5 - shown;
        Note(payload).Should().Be(
            $"{omitted} rows omitted to fit the payload cap; narrow with filters or select fewer columns");
        JsonSerializer.Serialize(payload).Length.Should().BeLessThanOrEqualTo(350);
    }

    // ---- crypto projection through the intent-read seam ---------------------------

    [Fact]
    public async Task Execute_EncryptedColumn_MaskedWithoutTheUnmaskRole_PlaintextWithIt()
    {
        await SeedOrderAsync(1, "alice", 10.5, CardPlaintext);
        await using (var cmd = new SqliteCommand("SELECT card_number FROM orders", _keepAlive))
        {
            var stored = (await cmd.ExecuteScalarAsync())?.ToString();
            stored.Should().NotBeNull().And.NotBe(CardPlaintext, "the column is ciphertext at rest");
        }

        using var unprivileged = await ExecuteAsync("{}", Tenant(1));
        using var privileged = await ExecuteAsync("{}", Tenant(1, UnmaskRole));

        var masked = unprivileged.RootElement.GetProperty("rows")[0].GetProperty("card_number").GetString();
        masked.Should().NotBe(CardPlaintext).And.NotContain("4111", "masked, never plaintext or ciphertext");
        privileged.RootElement.GetProperty("rows")[0].GetProperty("card_number").GetString()
            .Should().Be(CardPlaintext, "the unmask role decrypts through the intent-read seam");
    }

    // ---- model-visible input validation --------------------------------------------

    [Fact]
    public async Task Execute_UnknownFilterColumn_ThrowsNamingTheValidColumns()
    {
        var act = () => _connector.ExecuteAsync(
            "explore_orders", """{"filters":{"nope":{"_eq":1}}}""", Tenant(1), CancellationToken.None);

        (await act.Should().ThrowAsync<ChatToolInputException>())
            .Which.Message.Should().Contain("'nope'")
            .And.Contain("id, tenant_id, customer, total, created_at, card_number");
    }

    [Fact]
    public async Task Execute_InvalidOperatorForTheColumnType_ThrowsNamingTheValidOperators()
    {
        // _contains is a string operator; total is numeric.
        var act = () => _connector.ExecuteAsync(
            "explore_orders", """{"filters":{"total":{"_contains":"x"}}}""", Tenant(1), CancellationToken.None);

        (await act.Should().ThrowAsync<ChatToolInputException>())
            .Which.Message.Should().Contain("'_contains'").And.Contain("'total'")
            .And.Contain("_eq, _gt, _gte, _lt, _lte, _between");
    }

    [Fact]
    public async Task Execute_MalformedArguments_AreModelVisibleErrors()
    {
        Task Run(string inputJson) => _connector.ExecuteAsync(
            "explore_orders", inputJson, Tenant(1), CancellationToken.None);

        // Unknown top-level argument.
        (await ((Func<Task>)(() => Run("""{"limits":1}"""))).Should().ThrowAsync<ChatToolInputException>())
            .Which.Message.Should().Contain("'limits'").And.Contain("filters, sort, limit, offset, columns");
        // Unknown sort column names the valid columns.
        await ((Func<Task>)(() => Run("""{"sort":{"column":"nope"}}""")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*'nope'*customer*");
        // Bad sort direction.
        await ((Func<Task>)(() => Run("""{"sort":{"column":"total","direction":"sideways"}}""")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*asc*desc*");
        // Unknown projection column.
        await ((Func<Task>)(() => Run("""{"columns":["nope"]}""")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*'nope'*customer*");
        // _between needs exactly two bounds.
        await ((Func<Task>)(() => Run("""{"filters":{"total":{"_between":[1]}}}""")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*exactly two*");
        // Non-positive limit.
        await ((Func<Task>)(() => Run("""{"limit":0}""")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*'limit'*");
        // Not a JSON object at all.
        await ((Func<Task>)(() => Run("[]")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*JSON object*");
    }

    // ---- full tool loop over SSE fixtures -------------------------------------------

    private static RawMessageStreamEvent Event(string json) =>
        JsonSerializer.Deserialize<RawMessageStreamEvent>(json)!;

    private static RawMessageStreamEvent Start() =>
        Event("""
            {"type":"message_start","message":{"id":"msg_test","type":"message","role":"assistant",
             "model":"claude-opus-4-8","content":[],"stop_reason":null,"stop_sequence":null,
             "usage":{"input_tokens":1,"output_tokens":0} } }
            """);

    private static RawMessageStreamEvent TextDeltaEvent(string text) =>
        Event($$"""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{text}}"} }""");

    private static RawMessageStreamEvent ToolUseStart(string id, string name) =>
        Event($$"""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"{{id}}","name":"{{name}}","input":{} } }""");

    private static RawMessageStreamEvent InputJsonDeltaEvent(string partialJson) =>
        Event(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "content_block_delta",
            ["index"] = 0L,
            ["delta"] = new Dictionary<string, object?> { ["type"] = "input_json_delta", ["partial_json"] = partialJson },
        }));

    private static RawMessageStreamEvent BlockStop() =>
        Event("""{"type":"content_block_stop","index":0 }""");

    private static RawMessageStreamEvent FinalDelta(string stopReason) =>
        Event($$"""
            {"type":"message_delta","delta":{"stop_reason":"{{stopReason}}","stop_sequence":null},
             "usage":{"output_tokens":1 } }
            """);

    private static async IAsyncEnumerable<RawMessageStreamEvent> Stream(params RawMessageStreamEvent[] events)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            yield return e;
        }
    }

    [Fact]
    public async Task FullToolLoop_ModelCallsTheExploreTool_TenantScopedResultFedBack_EndTurn()
    {
        // Arrange: real registry + real connector over the SQLite executor; only the
        // Anthropic SDK boundary is scripted (same approach as ChatToolLoopTests).
        await SeedOrderAsync(1, "alice", 10.5);
        await SeedOrderAsync(2, "mallory", 99.0);
        var model = await _reads.GetModelAsync(EndpointPath);
        var toolSet = new ChatConnectorRegistry(new IChatConnector[] { _connector }).BuildToolSet(model);
        toolSet.Definitions.Should().ContainSingle().Which.Name.Should().Be("explore_orders");

        var turns = new Queue<IAsyncEnumerable<RawMessageStreamEvent>>(new[]
        {
            Stream(
                Start(),
                ToolUseStart("toolu_1", "explore_orders"),
                InputJsonDeltaEvent("""{"filters":{"customer":{"_contains":"ali"}}}"""),
                BlockStop(),
                FinalDelta("tool_use")),
            Stream(Start(), TextDeltaEvent("Alice has one order."), FinalDelta("end_turn")),
        });
        var requests = new List<MessageCreateParams>();
        var messages = Substitute.For<IMessageService>();
        messages.CreateStreaming(Arg.Any<MessageCreateParams>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                requests.Add(call.Arg<MessageCreateParams>());
                return turns.Dequeue();
            });
        var service = new AnthropicChatCompletionService(messages, new ChatCompletionOptions { ApiKey = "sk-test" });
        var options = new ChatCompletionRequestOptions
        {
            Tools = new ChatCompletionToolOptions
            {
                Tools = toolSet.Definitions,
                Executor = toolSet.CreateExecutor(Tenant(1)),
            },
        };

        // Act
        var events = new List<ChatCompletionEvent>();
        await foreach (var evt in service.StreamAsync(
            new[] { new ChatCompletionMessage(ChatMessageRoles.User, "Any orders for alice?") }, options))
            events.Add(evt);

        // Assert: the tool executed under tenant 1 and the result fed back verbatim.
        var activities = events.OfType<ChatToolActivity>().ToList();
        activities.Should().HaveCount(2);
        activities[0].Phase.Should().Be(ChatToolPhase.Call);
        activities[1].Phase.Should().Be(ChatToolPhase.Result);
        activities[1].Summary.Should().Contain("alice").And.NotContain("mallory");

        requests.Should().HaveCount(2);
        requests[1].Messages[2].Content.TryPickContentBlockParams(out var blocks).Should().BeTrue();
        blocks![0].TryPickToolResult(out var toolResult).Should().BeTrue();
        toolResult!.IsError.Should().NotBeTrue();
        toolResult.Content!.TryPickString(out var payload).Should().BeTrue();
        using var doc = JsonDocument.Parse(payload!);
        Customers(doc).Should().Equal("alice");

        events.Last().Should().BeOfType<ChatCompletionResult>()
            .Which.StopReason.Should().Be(ChatCompletionStopReason.Complete);
    }

    // ---- options validation ---------------------------------------------------------

    [Fact]
    public void ChatConnectorOptions_RejectInvalidCaps_FailFast()
    {
        var rowCap = () => new ChatConnectorOptions { ExploreRowCap = 0 };
        var charCap = () => new ChatConnectorOptions { ExplorePayloadCharCap = 0 };

        rowCap.Should().Throw<ArgumentOutOfRangeException>();
        charCap.Should().Throw<ArgumentOutOfRangeException>();
        new ChatConnectorOptions().ExploreRowCap.Should().Be(50);
        new ChatConnectorOptions().ExplorePayloadCharCap.Should().Be(20_000);
    }
}
