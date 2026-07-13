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
/// End-to-end proof of the media chat connector (connector slice 4): one
/// <c>media_&lt;table&gt;</c> lookup tool per <c>chat-connector: media</c> table,
/// sharing the explore filter/sort/limit input surface, returning the FIXED row
/// shape <c>{id, caption?, mediaReference}</c>. Pinned here: URL mode hands out
/// the stored URL and binary mode an opaque <c>bifrost-media://</c> reference (no
/// bytes in the payload), <see cref="ChatToolResult.MediaReferences"/> feeds the
/// transports, the vision path loads bytes through the intent read under the
/// caller's scope (size-capped, image-sniffed, fail-closed cross-tenant) and the
/// tool loop attaches them as a base64 image block — while vision-off schemas
/// carry no view input at all and bytes never reach the wire.
/// </summary>
public sealed class MediaChatConnectorTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_media_connector_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";

    private static readonly byte[] PngBytes =
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }
            .Concat(Enumerable.Repeat((byte)0x42, 24)).ToArray();

    private SqliteConnection _keepAlive = null!;
    private ServiceProvider _provider = null!;
    private IQueryIntentExecutor _reads = null!;
    private IMutationIntentExecutor _writes = null!;
    private MediaChatConnector _connector = null!;

    private static readonly string[] Rules =
    {
        // Binary mode + vision + caption; one encrypted and one hidden column to
        // prove the shared schema rules hold on the media surface too.
        "main.documents { chat-connector: media; chat-media-column: content; " +
            "chat-media-vision: enabled; chat-media-caption: caption; " +
            "chat-tool-description: Scanned contract pages.; tenant-filter: tenant_id }",
        "main.documents.owner_ssn { encrypt: aes-256-gcm; key-ref: config:docs; mask: redact }",
        "main.documents.internal_notes { visibility: hidden }",
        // Binary mode, vision OFF.
        "main.attachments { chat-connector: media; chat-media-column: data; tenant-filter: tenant_id }",
        // URL mode (string column), caption, no vision.
        "main.links { chat-connector: media; chat-media-column: url; " +
            "chat-media-caption: caption; tenant-filter: tenant_id }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var table in new[] { "documents", "attachments", "links" })
            await Exec($"DROP TABLE IF EXISTS {table}");
        await Exec(
            """
            CREATE TABLE documents (
                id             INTEGER PRIMARY KEY,
                tenant_id      INTEGER NOT NULL,
                caption        TEXT NULL,
                content        BLOB NULL,
                owner_ssn      TEXT NULL,
                internal_notes TEXT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE attachments (
                id        INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                data      BLOB NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE links (
                id        INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                caption   TEXT NULL,
                url       TEXT NULL
            )
            """);

        var root = new byte[FieldCipher.KeySize];
        for (var i = 0; i < root.Length; i++) root[i] = (byte)(i + 29);
        var keyManager = new EnvelopeKeyManager(
            new ConfigRootKeyProvider(root), new InMemoryDataEncryptionKeyStore());

        var services = new ServiceCollection();
        services.AddSingleton(keyManager);
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

        _connector = new MediaChatConnector(_reads, endpoint: EndpointPath);
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

    private static IDictionary<string, object?> Tenant(int tenantId) =>
        new Dictionary<string, object?> { ["tenant_id"] = tenantId };

    private async Task SeedAsync(string table, Dictionary<string, object?> data, int tenantId)
    {
        await _writes.ExecuteAsync(new MutationIntent
        {
            Table = table,
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase),
            UserContext = Tenant(tenantId),
            Endpoint = EndpointPath,
        });
    }

    private Task SeedDocumentAsync(int tenantId, string caption, byte[]? content) =>
        SeedAsync("documents",
            new Dictionary<string, object?> { ["caption"] = caption, ["content"] = content }, tenantId);

    private Task SeedLinkAsync(int tenantId, string caption, string? url) =>
        SeedAsync("links",
            new Dictionary<string, object?> { ["caption"] = caption, ["url"] = url }, tenantId);

    private async Task<ChatToolResult> ExecuteAsync(
        string toolName, string inputJson, IDictionary<string, object?> authContext,
        MediaChatConnector? connector = null)
    {
        var result = await (connector ?? _connector)
            .ExecuteAsync(toolName, inputJson, authContext, CancellationToken.None);
        result.IsError.Should().BeFalse();
        return result;
    }

    private async Task<ChatToolDefinition> DefinitionAsync(string tableName)
    {
        var model = await _reads.GetModelAsync(EndpointPath);
        var binding = ChatConnectorConfig.FromModel(model)
            .Single(b => b.Table.GraphQlName == tableName);
        return _connector.GetToolDefinitions(model, binding).Should().ContainSingle().Subject;
    }

    // ---- tool definitions ------------------------------------------------------

    [Fact]
    public async Task ToolDefinition_BinaryVisionTable_PinsNameDescriptionAndInputSchema()
    {
        var definition = await DefinitionAsync("documents");

        definition.Name.Should().Be("media_documents");
        definition.Description.Should().StartWith(
            "Call this when the user asks about the media (images/files) in documents (table main.documents).");
        definition.Description.Should().Contain("mediaReference");
        definition.Description.Should().Contain("view_image_id", "vision is enabled on this table");
        definition.Description.Should().EndWith(
            "Scanned contract pages.", "the chat-tool-description metadata steers the model");

        using var schema = JsonDocument.Parse(definition.InputSchemaJson);
        var root = schema.RootElement;
        root.GetProperty("type").GetString().Should().Be("object");
        root.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();

        // FIXED result shape: no columns projection argument at all.
        var properties = root.GetProperty("properties");
        properties.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "filters", "sort", "limit", "offset", "view_image_id");

        // Shared predicate rules: hidden and encrypted columns absent, and the
        // media content column is never a predicate either.
        var filterColumns = properties.GetProperty("filters").GetProperty("properties");
        filterColumns.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "tenant_id", "caption");
        properties.GetProperty("sort").GetProperty("properties").GetProperty("column")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo("id", "tenant_id", "caption");

        properties.GetProperty("view_image_id").GetProperty("type").GetString()
            .Should().Be("number", "the primary key is numeric");
    }

    [Fact]
    public async Task ToolDefinition_VisionOff_HasNoViewImageInput_BinaryAndUrlAlike()
    {
        foreach (var tableName in new[] { "attachments", "links" })
        {
            var definition = await DefinitionAsync(tableName);
            definition.InputSchemaJson.Should().NotContain("view_image_id",
                $"vision is off on {tableName}, so the input must not exist at all");
            definition.Description.Should().NotContain("view_image_id");
        }
    }

    [Fact]
    public async Task ToolDefinition_NonMediaBinding_YieldsNoTool()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore))
            .Build();
        var binding = ChatConnectorConfig.FromModel(model).Should().ContainSingle().Subject;

        _connector.GetToolDefinitions(model, binding).Should().BeEmpty();
    }

    // ---- lookup: binary vs URL reference shapes ----------------------------------

    [Fact]
    public async Task Lookup_BinaryMode_RowsCarryOpaqueReferences_AndNoBytes()
    {
        await SeedDocumentAsync(1, "contract page 1", PngBytes);
        await SeedDocumentAsync(1, "contract page 2", PngBytes);

        var result = await ExecuteAsync("media_documents", "{}", Tenant(1));

        using var payload = JsonDocument.Parse(result.TextPayload);
        var rows = payload.RootElement.GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(2);
        rows[0].EnumerateObject().Select(p => p.Name).Should().Equal("id", "caption", "mediaReference");
        rows[0].GetProperty("id").GetInt64().Should().Be(1);
        rows[0].GetProperty("caption").GetString().Should().Be("contract page 1");
        rows[0].GetProperty("mediaReference").GetString().Should().Be("bifrost-media://documents/1");
        rows[1].GetProperty("mediaReference").GetString().Should().Be("bifrost-media://documents/2");

        // The payload never carries the blob — not raw, not base64.
        result.TextPayload.Should().NotContain(Convert.ToBase64String(PngBytes));

        result.MediaReferences.Should().HaveCount(2);
        var reference = result.MediaReferences![0];
        reference.Table.Should().Be("documents");
        reference.Column.Should().Be("content");
        reference.RowId.Should().Be(1L);
        reference.ContentType.Should().BeNull("the lookup never loads bytes, so nothing was sniffed");
        reference.MediaReference.Should().Be("bifrost-media://documents/1");
        reference.Caption.Should().Be("contract page 1");
        result.VisionImage.Should().BeNull();
    }

    [Fact]
    public async Task Lookup_UrlMode_RowsCarryTheStoredUrls_NullUrlYieldsNoStreamReference()
    {
        await SeedLinkAsync(1, "logo", "https://cdn.example.com/logo.png");
        await SeedLinkAsync(1, "pending", null);

        var result = await ExecuteAsync("media_links", "{}", Tenant(1));

        using var payload = JsonDocument.Parse(result.TextPayload);
        var rows = payload.RootElement.GetProperty("rows").EnumerateArray().ToList();
        rows[0].GetProperty("mediaReference").GetString().Should().Be("https://cdn.example.com/logo.png");
        rows[1].GetProperty("mediaReference").ValueKind.Should().Be(JsonValueKind.Null,
            "the model must see that the row has no media");

        var reference = result.MediaReferences.Should().ContainSingle(
            "a null URL has nothing a client could fetch").Subject;
        reference.MediaReference.Should().Be("https://cdn.example.com/logo.png");
        reference.Caption.Should().Be("logo");
        reference.RowId.Should().Be(1L);
    }

    [Fact]
    public async Task Lookup_IsTenantScoped_FailClosedBothDirections()
    {
        await SeedDocumentAsync(1, "mine", PngBytes);
        await SeedDocumentAsync(2, "theirs", PngBytes);

        var tenant1 = await ExecuteAsync("media_documents", "{}", Tenant(1));
        var tenant2 = await ExecuteAsync("media_documents", "{}", Tenant(2));

        tenant1.TextPayload.Should().Contain("mine").And.NotContain("theirs");
        tenant2.TextPayload.Should().Contain("theirs").And.NotContain("mine");
    }

    [Fact]
    public async Task Lookup_FiltersSortLimitOffset_ShapeTheQuery()
    {
        await SeedLinkAsync(1, "alpha", "https://example.com/a");
        await SeedLinkAsync(1, "beta", "https://example.com/b");
        await SeedLinkAsync(1, "gamma", "https://example.com/c");

        var result = await ExecuteAsync("media_links",
            """{"filters":{"caption":{"_contains":"a"}},"sort":{"column":"caption","direction":"desc"},"limit":2}""",
            Tenant(1));

        using var payload = JsonDocument.Parse(result.TextPayload);
        payload.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("caption").GetString())
            .Should().Equal("gamma", "beta");
    }

    [Fact]
    public async Task Lookup_RowCapTrims_ReportsTheOmissionExplicitly()
    {
        for (var i = 1; i <= 4; i++)
            await SeedLinkAsync(1, $"link-{i}", $"https://example.com/{i}");
        var capped = new MediaChatConnector(
            _reads, new ChatConnectorOptions { ExploreRowCap = 2 }, EndpointPath);

        var result = await ExecuteAsync("media_links", "{}", Tenant(1), capped);

        using var payload = JsonDocument.Parse(result.TextPayload);
        payload.RootElement.GetProperty("rows").GetArrayLength().Should().Be(2);
        payload.RootElement.GetProperty("note").GetString()
            .Should().Be("showing 2 of at least 3 rows; narrow with filters");
        result.MediaReferences.Should().HaveCount(2, "trimmed rows must not leak references either");
    }

    // ---- model-visible input validation ---------------------------------------------

    [Fact]
    public async Task MediaColumn_IsRejectedAsPredicate_WithAnAuthoredMessage()
    {
        var filter = () => _connector.ExecuteAsync(
            "media_links", """{"filters":{"url":{"_eq":"x"}}}""", Tenant(1), CancellationToken.None);
        var sort = () => _connector.ExecuteAsync(
            "media_links", """{"sort":{"column":"url"}}""", Tenant(1), CancellationToken.None);

        (await filter.Should().ThrowAsync<ChatToolInputException>())
            .Which.Message.Should().Be("Column 'url' cannot be used in 'filters'; it is the media content column.");
        (await sort.Should().ThrowAsync<ChatToolInputException>())
            .Which.Message.Should().Be("Column 'url' cannot be used in 'sort'; it is the media content column.");
    }

    [Fact]
    public async Task HiddenAndEncryptedColumns_KeepTheSharedRules()
    {
        // Hidden: rejected as unknown without disclosure; encrypted: authored rejection.
        var hidden = () => _connector.ExecuteAsync(
            "media_documents", """{"filters":{"internal_notes":{"_eq":"x"}}}""", Tenant(1), CancellationToken.None);
        var encrypted = () => _connector.ExecuteAsync(
            "media_documents", """{"sort":{"column":"owner_ssn"}}""", Tenant(1), CancellationToken.None);

        (await hidden.Should().ThrowAsync<ChatToolInputException>())
            .Which.Message.Should().Contain("Unknown column 'internal_notes'").And.NotContain("hidden");
        (await encrypted.Should().ThrowAsync<ChatToolInputException>())
            .Which.Message.Should().Be("Column 'owner_ssn' cannot be used in 'sort'; it is encrypted.");
    }

    [Fact]
    public async Task UnknownArguments_NameTheValidOnes_PerVisionMode()
    {
        var visionOn = () => _connector.ExecuteAsync(
            "media_documents", """{"columns":["caption"]}""", Tenant(1), CancellationToken.None);
        var visionOff = () => _connector.ExecuteAsync(
            "media_links", """{"view_image_id":1}""", Tenant(1), CancellationToken.None);

        (await visionOn.Should().ThrowAsync<ChatToolInputException>())
            .Which.Message.Should().Contain("'columns'")
            .And.Contain("filters, sort, limit, offset, view_image_id");
        // Vision off: view_image_id is as unknown as any other invented argument.
        (await visionOff.Should().ThrowAsync<ChatToolInputException>())
            .Which.Message.Should().Contain("'view_image_id'")
            .And.Contain("Valid arguments: filters, sort, limit, offset.");
    }

    [Fact]
    public async Task ViewImageId_CombinedWithLookupArguments_IsRejected()
    {
        var act = () => _connector.ExecuteAsync(
            "media_documents", """{"view_image_id":1,"limit":5}""", Tenant(1), CancellationToken.None);

        (await act.Should().ThrowAsync<ChatToolInputException>())
            .Which.Message.Should().Contain("cannot be combined");
    }

    // ---- vision ----------------------------------------------------------------------

    [Fact]
    public async Task ViewImage_LoadsTheBytes_SniffsTheType_AndReportsTheAttachment()
    {
        await SeedDocumentAsync(1, "contract page 1", PngBytes);

        var result = await ExecuteAsync("media_documents", """{"view_image_id":1}""", Tenant(1));

        result.VisionImage.Should().NotBeNull();
        result.VisionImage!.Data.Should().Equal(PngBytes);
        result.VisionImage.MediaType.Should().Be("image/png");

        using var payload = JsonDocument.Parse(result.TextPayload);
        var row = payload.RootElement.GetProperty("rows").EnumerateArray().Should().ContainSingle().Subject;
        row.GetProperty("id").GetInt64().Should().Be(1);
        row.GetProperty("caption").GetString().Should().Be("contract page 1");
        row.GetProperty("contentType").GetString().Should().Be("image/png");
        row.GetProperty("mediaReference").GetString().Should().Be("bifrost-media://documents/1");
        payload.RootElement.GetProperty("note").GetString()
            .Should().Be("the image is attached to this tool result as vision input");

        var reference = result.MediaReferences.Should().ContainSingle().Subject;
        reference.ContentType.Should().Be("image/png", "the vision load sniffed the bytes");
    }

    [Fact]
    public async Task ViewImage_CrossTenantOrMissingId_IsTheSameFailClosedError()
    {
        await SeedDocumentAsync(2, "theirs", PngBytes);

        Task Run() => _connector.ExecuteAsync(
            "media_documents", """{"view_image_id":1}""", Tenant(1), CancellationToken.None);
        Task RunMissing() => _connector.ExecuteAsync(
            "media_documents", """{"view_image_id":999}""", Tenant(1), CancellationToken.None);

        var crossTenant = (await ((Func<Task>)Run).Should().ThrowAsync<ChatToolInputException>()).Which.Message;
        var missing = (await ((Func<Task>)RunMissing).Should().ThrowAsync<ChatToolInputException>()).Which.Message;
        crossTenant.Should().Be("No documents row with id = '1' is visible to you.");
        missing.Should().Be("No documents row with id = '999' is visible to you.");
    }

    [Fact]
    public async Task ViewImage_OverTheByteCap_IsAnExplicitModelVisibleError()
    {
        await SeedDocumentAsync(1, "big", PngBytes);
        var capped = new MediaChatConnector(
            _reads, new ChatConnectorOptions { MediaVisionByteCap = 16 }, EndpointPath);

        var act = () => capped.ExecuteAsync(
            "media_documents", """{"view_image_id":1}""", Tenant(1), CancellationToken.None);

        (await act.Should().ThrowAsync<ChatToolInputException>()).Which.Message.Should().Be(
            $"The image is {PngBytes.Length} bytes, over the 16-byte vision cap; " +
            "it cannot be attached as vision input.");
    }

    [Fact]
    public async Task ViewImage_UnrecognizedFormat_IsRejected()
    {
        await SeedDocumentAsync(1, "not an image", new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 });

        var act = () => _connector.ExecuteAsync(
            "media_documents", """{"view_image_id":1}""", Tenant(1), CancellationToken.None);

        (await act.Should().ThrowAsync<ChatToolInputException>())
            .Which.Message.Should().Contain("not a recognized image format (png, jpeg, gif, webp)");
    }

    [Fact]
    public async Task ViewImage_NullContent_IsAModelVisibleError()
    {
        await SeedDocumentAsync(1, "empty", null);

        var act = () => _connector.ExecuteAsync(
            "media_documents", """{"view_image_id":1}""", Tenant(1), CancellationToken.None);

        (await act.Should().ThrowAsync<ChatToolInputException>())
            .Which.Message.Should().Be("The documents row with id = '1' has no media content.");
    }

    // ---- the tool loop: vision blocks + media events over SSE fixtures ----------------

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

    private async Task<(List<ChatCompletionEvent> Events, List<MessageCreateParams> Requests)> RunLoopAsync(
        string toolName, string toolInputJson, string question)
    {
        var model = await _reads.GetModelAsync(EndpointPath);
        var toolSet = new ChatConnectorRegistry(new IChatConnector[] { _connector }).BuildToolSet(model);

        var turns = new Queue<IAsyncEnumerable<RawMessageStreamEvent>>(new[]
        {
            Stream(
                Start(),
                ToolUseStart("toolu_1", toolName),
                InputJsonDeltaEvent(toolInputJson),
                BlockStop(),
                FinalDelta("tool_use")),
            Stream(Start(), TextDeltaEvent("Done."), FinalDelta("end_turn")),
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

        var events = new List<ChatCompletionEvent>();
        await foreach (var evt in service.StreamAsync(
            new[] { new ChatCompletionMessage(ChatMessageRoles.User, question) }, options))
            events.Add(evt);
        return (events, requests);
    }

    [Fact]
    public async Task ToolLoop_VisionOn_TheBytesReachTheModelAsABase64ImageBlock()
    {
        await SeedDocumentAsync(1, "contract page 1", PngBytes);

        var (events, requests) = await RunLoopAsync(
            "media_documents", """{"view_image_id":1}""", "What does the contract page say?");

        // The tool_result of the continuation request is a block list: the JSON
        // payload text plus one base64 image block with the sniffed media type.
        requests.Should().HaveCount(2);
        requests[1].Messages[2].Content.TryPickContentBlockParams(out var blocks).Should().BeTrue();
        blocks![0].TryPickToolResult(out var toolResult).Should().BeTrue();
        toolResult!.IsError.Should().NotBeTrue();
        toolResult.Content!.TryPickBlocks(out var contentBlocks).Should().BeTrue(
            "a vision-bearing result must be a content block list, not a plain string");
        contentBlocks.Should().HaveCount(2);
        contentBlocks![0].TryPickTextBlockParam(out var text).Should().BeTrue();
        text!.Text.Should().Contain("bifrost-media://documents/1");
        contentBlocks[1].TryPickImageBlockParam(out var image).Should().BeTrue();
        image!.Source.TryPickBase64Image(out var source).Should().BeTrue();
        source!.Data.Should().Be(Convert.ToBase64String(PngBytes));
        source.MediaType.Raw().Should().Be("image/png");

        // The stream reported the media reference for the transport to relay.
        var media = events.OfType<ChatToolMediaActivity>().Should().ContainSingle().Subject;
        media.ToolName.Should().Be("media_documents");
        media.Items.Should().ContainSingle().Which.MediaReference.Should().Be("bifrost-media://documents/1");

        events.Last().Should().BeOfType<ChatCompletionResult>()
            .Which.StopReason.Should().Be(ChatCompletionStopReason.Complete);
    }

    [Fact]
    public async Task ToolLoop_VisionOff_NoBytesEverReachTheWire()
    {
        await SeedAsync("attachments", new Dictionary<string, object?> { ["data"] = PngBytes }, 1);

        var (events, requests) = await RunLoopAsync(
            "media_attachments", "{}", "What attachments are there?");

        // The schema carries no view input, the tool result stays a plain string,
        // and NOTHING in any captured wire request contains the bytes.
        requests.Should().HaveCount(2);
        requests[1].Messages[2].Content.TryPickContentBlockParams(out var blocks).Should().BeTrue();
        blocks![0].TryPickToolResult(out var toolResult).Should().BeTrue();
        toolResult!.Content!.TryPickString(out var payload).Should().BeTrue(
            "a text-only result must stay the plain string shape");
        payload.Should().Contain("bifrost-media://attachments/1");

        var base64 = Convert.ToBase64String(PngBytes);
        foreach (var request in requests)
            JsonSerializer.Serialize(request).Should().NotContain(base64);

        // The media references still stream for the client.
        events.OfType<ChatToolMediaActivity>().Should().ContainSingle()
            .Which.Items.Should().ContainSingle()
            .Which.MediaReference.Should().Be("bifrost-media://attachments/1");
    }

    // ---- options ----------------------------------------------------------------------

    [Fact]
    public void MediaVisionByteCap_DefaultsAndRejectsInvalidValues()
    {
        new ChatConnectorOptions().MediaVisionByteCap.Should().Be(3_500_000);
        var act = () => new ChatConnectorOptions { MediaVisionByteCap = 0 };
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
