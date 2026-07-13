using Anthropic.Models.Messages;
using Anthropic.Services;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Chat;
using BifrostQL.Core.Modules.History;
using BifrostQL.Core.QueryModel;
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
/// End-to-end proof of the plan chat connector (connector slice 5 — the
/// security-critical one: LLM-initiated writes, human-gated). A blog posts table
/// with a publish schedule is a plan connector; the pinned contract is epic
/// decision D2:
///
/// <list type="bullet">
/// <item>a plan tool call NEVER writes — it produces a proposal;</item>
/// <item>a confirmed proposal executes through the batch mutation-intent seam under
/// the ORIGINAL caller (tenant stamp, audit stamp, history trail compose), ALL rows
/// in ONE transaction — a veto anywhere writes NOTHING;</item>
/// <item>a denied or timed-out proposal feeds a declined (non-error) result back
/// and the model continues;</item>
/// <item>the confirmation id is single-use and bound to identity + conversation;</item>
/// <item>a disallowed operation's tool is ABSENT from the schema.</item>
/// </list>
/// </summary>
public sealed class PlanChatConnectorTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_plan_connector_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private const string Conversation = "42";

    private SqliteConnection _keepAlive = null!;
    private ServiceProvider _provider = null!;
    private IQueryIntentExecutor _reads = null!;
    private IMutationIntentExecutor _writes = null!;
    private ChatPlanConfirmationRegistry _registry = null!;
    private PlanChatConnector _connector = null!;

    private static readonly string[] Rules =
    {
        "main.posts { chat-connector: plan; chat-plan-operations: insert,update,delete; " +
            "tenant-filter: tenant_id; history: enabled; history-columns: title,publish_at,status; " +
            "chat-tool-description: Blog posts with their publish schedule. }",
        "main.posts.created_at { populate: created-on }",
        "main.posts.internal_notes { visibility: hidden }",
        // A second plan table whose allow-list omits update/delete: their tools must
        // not exist, and calling one is an unknown tool.
        "main.drafts { chat-connector: plan; chat-plan-operations: insert; tenant-filter: tenant_id }",
        ":root { history-table: main.__history }",
    };

    /// <summary>Vetoes any write whose title is 'veto-me' — the no-partial-batch probe.</summary>
    private sealed class VetoTitleTransformer : IMutationTransformer
    {
        public int Priority => 60;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context) => true;

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data,
            MutationTransformContext context)
        {
            var vetoed = data.TryGetValue("title", out var title) && title as string == "veto-me";
            return ValueTask.FromResult(new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
                Errors = vetoed ? new[] { "the title 'veto-me' is not allowed" } : Array.Empty<string>(),
            });
        }
    }

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var table in new[] { "posts", "drafts", "__history" })
            await Exec($"DROP TABLE IF EXISTS {table}");
        await Exec(
            """
            CREATE TABLE posts (
                id         INTEGER PRIMARY KEY,
                tenant_id  INTEGER NOT NULL,
                title      TEXT NULL,
                publish_at DATETIME NULL,
                status     TEXT NULL,
                created_at DATETIME NULL,
                internal_notes TEXT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE drafts (
                id        INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                title     TEXT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE __history (
                id              INTEGER PRIMARY KEY,
                entity          TEXT NOT NULL,
                entity_id       TEXT NOT NULL,
                op              TEXT NOT NULL,
                actor           TEXT NULL,
                changed_at      TEXT NOT NULL,
                before          TEXT NULL,
                after           TEXT NULL,
                changed_columns TEXT NULL,
                tenant_id       TEXT NULL
            )
            """);

        // The history writer registered exactly as the host DI does, so a confirmed
        // plan write records its trail rows in the same transaction.
        var services = new ServiceCollection();
        services.AddSingleton<HistoryMutationHook>();
        services.AddSingleton<IBeforeCommitMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<IInTransactionMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton(sp => new BeforeCommitMutationHooks(
            sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        services.AddSingleton(sp => new InTransactionMutationHooks(
            sp.GetServices<IInTransactionMutationHook>().ToArray()));
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
                Transformers = new IFilterTransformer[] { new TenantFilterTransformer() },
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
                    new AuditMutationTransformer(),
                    new VetoTitleTransformer(),
                },
            },
            _provider);

        _registry = new ChatPlanConfirmationRegistry();
        _connector = new PlanChatConnector(_reads, _writes, _registry, endpoint: EndpointPath);
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

    private async Task<long> CountAsync(string table, string where = "1=1")
    {
        await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM {table} WHERE {where}", _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        var value = await cmd.ExecuteScalarAsync();
        return value == DBNull.Value ? null : value;
    }

    private static IDictionary<string, object?> Caller(int tenantId, string conversation = Conversation) =>
        new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId,
            ["user_id"] = $"user-{tenantId}",
            [ChatPlanConfirmationRegistry.ConversationContextKey] = conversation,
        };

    private static string IdentityOf(int tenantId) =>
        ChatPlanConfirmationRegistry.RequireIdentityKey(Caller(tenantId));

    private async Task<ChatToolResult> ProposeAsync(
        string toolName, string inputJson, IDictionary<string, object?>? caller = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _connector.ExecuteAsync(
            toolName, inputJson, caller ?? Caller(1), cancellationToken);
        result.IsError.Should().BeFalse();
        result.ConfirmationRequest.Should().NotBeNull("a plan tool call must produce a write proposal");
        return result;
    }

    private const string TwoPostRows =
        """
        {"rows":[
          {"title":"Launch note","publish_at":"2026-08-01 10:00:00","status":"scheduled"},
          {"title":"Follow-up","publish_at":"2026-08-08 10:00:00","status":"draft"}
        ]}
        """;

    // ---- tool definitions -----------------------------------------------------------

    [Fact]
    public async Task ToolDefinitions_OneToolPerAllowedOperation_AllRequireConfirmation()
    {
        var model = await _reads.GetModelAsync(EndpointPath);
        var binding = ChatConnectorConfig.FromModel(model).Single(b => b.Table.DbName == "posts");

        var definitions = _connector.GetToolDefinitions(model, binding);

        definitions.Select(d => d.Name).Should().Equal(
            "plan_insert_posts", "plan_update_posts", "plan_delete_posts");
        definitions.Should().OnlyContain(d => d.RequiresConfirmation,
            "the tool loop must serialize and park on plan tools");
        definitions[0].Description.Should().Contain("does NOT write anything")
            .And.Contain("user must approve")
            .And.EndWith("Blog posts with their publish schedule.");
    }

    [Fact]
    public async Task ToolDefinitions_DisallowedOperations_AreAbsentFromTheSchema()
    {
        // drafts allows insert only: update/delete tools must not exist AT ALL —
        // absent from the schema, not present-and-refused.
        var model = await _reads.GetModelAsync(EndpointPath);
        var binding = ChatConnectorConfig.FromModel(model).Single(b => b.Table.DbName == "drafts");

        var definitions = _connector.GetToolDefinitions(model, binding);

        definitions.Select(d => d.Name).Should().Equal("plan_insert_drafts");
    }

    [Fact]
    public void ToolDefinitions_NonPlanBinding_YieldsNoTool()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("docs", t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore))
            .Build();
        var binding = ChatConnectorConfig.FromModel(model).Should().ContainSingle().Subject;

        _connector.GetToolDefinitions(model, binding).Should().BeEmpty();
    }

    [Fact]
    public async Task InputSchemas_PinTheWritableSurfacePerOperation()
    {
        var model = await _reads.GetModelAsync(EndpointPath);
        var binding = ChatConnectorConfig.FromModel(model).Single(b => b.Table.DbName == "posts");
        var definitions = _connector.GetToolDefinitions(model, binding)
            .ToDictionary(d => d.Name, d => JsonDocument.Parse(d.InputSchemaJson));

        static JsonElement RowSchema(JsonDocument schema) =>
            schema.RootElement.GetProperty("properties").GetProperty("rows").GetProperty("items");
        static List<string> Properties(JsonElement rowSchema) =>
            rowSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

        // Insert: identity key and hidden column absent; every writable column present.
        var insert = RowSchema(definitions["plan_insert_posts"]);
        Properties(insert).Should().BeEquivalentTo("tenant_id", "title", "publish_at", "status", "created_at");
        insert.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        insert.TryGetProperty("required", out _).Should().BeFalse();

        // Update: primary key required, plus the settable columns.
        var update = RowSchema(definitions["plan_update_posts"]);
        Properties(update).Should().BeEquivalentTo("id", "tenant_id", "title", "publish_at", "status", "created_at");
        update.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("id");

        // Delete: the primary key ONLY.
        var delete = RowSchema(definitions["plan_delete_posts"]);
        Properties(delete).Should().Equal("id");
        delete.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("id");

        // Rows are capped in the schema itself.
        definitions["plan_insert_posts"].RootElement.GetProperty("properties").GetProperty("rows")
            .GetProperty("maxItems").GetInt32().Should().Be(ChatConnectorOptions.DefaultPlanRowCap);

        foreach (var schema in definitions.Values)
            schema.Dispose();
    }

    // ---- a plan tool call NEVER writes ------------------------------------------------

    [Fact]
    public async Task Execute_ProducesAProposal_AndWritesNothing()
    {
        var result = await ProposeAsync("plan_insert_posts", TwoPostRows);

        var request = result.ConfirmationRequest!;
        request.ConfirmationId.Should().NotBeNullOrWhiteSpace();
        request.Table.Should().Be("posts");
        request.Operation.Should().Be("insert");
        request.Summary.Should().Be("insert 2 rows into main.posts");
        request.Rows.Should().HaveCount(2);
        request.Rows[0]["title"].Should().Be("Launch note");

        // THE pinned guarantee: proposing wrote NOTHING.
        (await CountAsync("posts")).Should().Be(0);
        (await CountAsync("__history")).Should().Be(0);
        _registry.PendingCount.Should().Be(1);
    }

    // ---- confirm: one transaction, full pipeline composition --------------------------

    [Fact]
    public async Task Confirm_PersistsRows_WithTenantStamp_AuditStamp_AndHistoryTrail()
    {
        var result = await ProposeAsync("plan_insert_posts", TwoPostRows);
        var request = result.ConfirmationRequest!;
        var resolving = request.ResolveAsync(CancellationToken.None);

        _registry.TryResolve(request.ConfirmationId, IdentityOf(1),
            Conversation, new ChatPlanDecision(true, null)).Should().BeTrue();
        var outcome = await resolving;

        outcome.Approved.Should().BeTrue();
        outcome.Result.IsError.Should().BeFalse();
        using var payload = JsonDocument.Parse(outcome.Result.TextPayload);
        payload.RootElement.GetProperty("approved").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("affected").GetInt32().Should().Be(2);

        // Rows landed with the tenant transformer's stamp and the audit populate.
        (await CountAsync("posts", "tenant_id = 1")).Should().Be(2);
        (await ScalarAsync("SELECT created_at FROM posts WHERE title = 'Launch note'"))
            .Should().NotBeNull("populate: created-on stamps inserts");
        // And the history hook wrote a trail row per insert, in the same transaction.
        (await CountAsync("__history", "op = 'insert'")).Should().Be(2);

        _registry.PendingCount.Should().Be(0, "a resolved confirmation is single-use");
    }

    [Fact]
    public async Task Confirm_Update_AddressesRowsByPrimaryKey_TenantScoped()
    {
        await Exec("INSERT INTO posts(id, tenant_id, title, status) VALUES (7, 1, 'Old title', 'draft')");
        await Exec("INSERT INTO posts(id, tenant_id, title, status) VALUES (8, 2, 'Other tenant', 'draft')");

        var result = await ProposeAsync("plan_update_posts",
            """{"rows":[{"id":7,"status":"scheduled","publish_at":"2026-08-01 10:00:00"}]}""");
        var request = result.ConfirmationRequest!;
        request.Summary.Should().Be("update 1 row in main.posts");
        var resolving = request.ResolveAsync(CancellationToken.None);
        _registry.TryResolve(request.ConfirmationId, IdentityOf(1),
            Conversation, new ChatPlanDecision(true, null)).Should().BeTrue();

        var outcome = await resolving;

        outcome.Result.IsError.Should().BeFalse();
        (await ScalarAsync("SELECT status FROM posts WHERE id = 7")).Should().Be("scheduled");
        (await ScalarAsync("SELECT status FROM posts WHERE id = 8")).Should().Be("draft");
    }

    [Fact]
    public async Task Confirm_CrossTenantUpdate_IsAZeroRowNoOp_NeverAnotherTenantsRow()
    {
        await Exec("INSERT INTO posts(id, tenant_id, title, status) VALUES (9, 2, 'Not yours', 'draft')");

        // Tenant 1 proposes an update to tenant 2's row: the tenant transformer's
        // WHERE scope makes the confirmed write affect zero rows — fail closed.
        var result = await ProposeAsync("plan_update_posts",
            """{"rows":[{"id":9,"status":"scheduled"}]}""");
        var request = result.ConfirmationRequest!;
        var resolving = request.ResolveAsync(CancellationToken.None);
        _registry.TryResolve(request.ConfirmationId, IdentityOf(1),
            Conversation, new ChatPlanDecision(true, null)).Should().BeTrue();

        var outcome = await resolving;

        using var payload = JsonDocument.Parse(outcome.Result.TextPayload);
        payload.RootElement.GetProperty("affected").GetInt32().Should().Be(0);
        (await ScalarAsync("SELECT status FROM posts WHERE id = 9")).Should().Be("draft");
    }

    [Fact]
    public async Task Confirm_Delete_RemovesTheRow()
    {
        await Exec("INSERT INTO posts(id, tenant_id, title) VALUES (5, 1, 'Retire me')");

        var result = await ProposeAsync("plan_delete_posts", """{"rows":[{"id":5}]}""");
        var request = result.ConfirmationRequest!;
        request.Summary.Should().Be("delete 1 row from main.posts");
        var resolving = request.ResolveAsync(CancellationToken.None);
        _registry.TryResolve(request.ConfirmationId, IdentityOf(1),
            Conversation, new ChatPlanDecision(true, null)).Should().BeTrue();

        (await resolving).Result.IsError.Should().BeFalse();
        (await CountAsync("posts", "id = 5")).Should().Be(0);
    }

    // ---- deny: nothing persists, the model continues ----------------------------------

    [Fact]
    public async Task Deny_PersistsNothing_AndFeedsADeclinedNonErrorResult()
    {
        var result = await ProposeAsync("plan_insert_posts", TwoPostRows);
        var request = result.ConfirmationRequest!;
        var resolving = request.ResolveAsync(CancellationToken.None);

        _registry.TryResolve(request.ConfirmationId, IdentityOf(1),
            Conversation, new ChatPlanDecision(false, "wrong publish dates")).Should().BeTrue();
        var outcome = await resolving;

        outcome.Approved.Should().BeFalse();
        outcome.Reason.Should().Be("wrong publish dates");
        outcome.Result.IsError.Should().BeFalse("a denial is an answer the model continues from, not a fault");
        using var payload = JsonDocument.Parse(outcome.Result.TextPayload);
        payload.RootElement.GetProperty("approved").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("reason").GetString().Should().Be("wrong publish dates");

        (await CountAsync("posts")).Should().Be(0);
        (await CountAsync("__history")).Should().Be(0);
    }

    [Fact]
    public async Task Timeout_DeniesTheProposal_NothingPersisted()
    {
        var quick = new PlanChatConnector(_reads, _writes, _registry,
            new ChatConnectorOptions { PlanConfirmationTimeout = TimeSpan.FromMilliseconds(100) },
            EndpointPath);
        var result = await quick.ExecuteAsync(
            "plan_insert_posts", TwoPostRows, Caller(1), CancellationToken.None);

        var outcome = await result.ConfirmationRequest!.ResolveAsync(CancellationToken.None);

        outcome.Approved.Should().BeFalse();
        outcome.Result.IsError.Should().BeFalse();
        outcome.Result.TextPayload.Should().Contain("timed out");
        (await CountAsync("posts")).Should().Be(0);
        _registry.PendingCount.Should().Be(0, "a timed-out confirmation is gone");
    }

    [Fact]
    public async Task RequestCancellation_KillsTheProposal_NothingPersisted()
    {
        using var cts = new CancellationTokenSource();
        var result = await ProposeAsync("plan_insert_posts", TwoPostRows, cancellationToken: cts.Token);
        var resolving = result.ConfirmationRequest!.ResolveAsync(cts.Token);

        cts.Cancel();

        await ((Func<Task>)(() => resolving)).Should().ThrowAsync<OperationCanceledException>();
        _registry.PendingCount.Should().Be(0, "the confirmation id expires with the request");
        (await CountAsync("posts")).Should().Be(0);
    }

    // ---- confirmation id binding: single-use, identity, conversation -------------------

    [Fact]
    public async Task ConfirmationId_WrongIdentity_WrongConversation_AndReuse_AllFailIdentically()
    {
        var result = await ProposeAsync("plan_insert_posts", TwoPostRows);
        var id = result.ConfirmationRequest!.ConfirmationId;
        var approve = new ChatPlanDecision(true, null);

        // Another caller's identity: refused, and the entry survives.
        _registry.TryResolve(id, IdentityOf(2), Conversation, approve).Should().BeFalse();
        // Another conversation: refused, and the entry survives.
        _registry.TryResolve(id, IdentityOf(1), "77", approve).Should().BeFalse();
        _registry.PendingCount.Should().Be(1, "a mismatched resolve must not burn the entry");
        // Unknown id: refused.
        _registry.TryResolve("no-such-id", IdentityOf(1), Conversation, approve).Should().BeFalse();

        // The right caller resolves it — exactly once.
        var resolving = result.ConfirmationRequest.ResolveAsync(CancellationToken.None);
        _registry.TryResolve(id, IdentityOf(1), Conversation, new ChatPlanDecision(false, null)).Should().BeTrue();
        _registry.TryResolve(id, IdentityOf(1), Conversation, approve).Should().BeFalse("single-use");
        (await resolving).Approved.Should().BeFalse();
    }

    // ---- transformer veto on confirm: no partial batch ---------------------------------

    [Fact]
    public async Task Veto_OnAnyRow_WritesNothing_AndFeedsASanitizedErrorResult()
    {
        // Row 1 is fine; row 2 trips the veto transformer. The batch is ONE
        // transaction, so row 1 must roll back with it — no partial batch.
        var result = await ProposeAsync("plan_insert_posts",
            """
            {"rows":[
              {"title":"Fine","status":"draft"},
              {"title":"veto-me","status":"draft"}
            ]}
            """);
        var request = result.ConfirmationRequest!;
        var resolving = request.ResolveAsync(CancellationToken.None);
        _registry.TryResolve(request.ConfirmationId, IdentityOf(1),
            Conversation, new ChatPlanDecision(true, null)).Should().BeTrue();

        var outcome = await resolving;

        outcome.Approved.Should().BeTrue("the user approved; the SERVER vetoed");
        outcome.Result.IsError.Should().BeTrue();
        outcome.Result.TextPayload.Should().Contain("plan_insert_posts")
            .And.Contain("NO rows were written")
            .And.NotContain("veto-me", "pipeline error detail is sanitized off the model channel");

        (await CountAsync("posts")).Should().Be(0, "no partial batch — the whole transaction rolled back");
        (await CountAsync("__history")).Should().Be(0, "the trail rolled back with it");
    }

    // ---- validation-first (the model is an untrusted caller) ---------------------------

    [Fact]
    public async Task Execute_InvalidInputs_AreModelVisibleErrors()
    {
        Task Run(string toolName, string inputJson) =>
            _connector.ExecuteAsync(toolName, inputJson, Caller(1), CancellationToken.None);

        // A disallowed operation's tool does not exist.
        await ((Func<Task>)(() => Run("plan_update_drafts", """{"rows":[{"id":1,"title":"x"}]}""")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*Unknown plan tool*");
        // Unknown column, naming only visible columns.
        var unknown = (await ((Func<Task>)(() => Run("plan_insert_posts", """{"rows":[{"nope":1}]}""")))
            .Should().ThrowAsync<ChatToolInputException>()).Which.Message;
        unknown.Should().Contain("'nope'").And.Contain("title").And.NotContain("internal_notes");
        // Hidden columns get the identical unknown-column rejection.
        (await ((Func<Task>)(() => Run("plan_insert_posts", """{"rows":[{"internal_notes":"x"}]}""")))
            .Should().ThrowAsync<ChatToolInputException>()).Which.Message.Should().Contain("Unknown column");
        // Database-generated columns are not writable.
        await ((Func<Task>)(() => Run("plan_insert_posts", """{"rows":[{"id":1,"title":"x"}]}""")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*generated by the database*");
        // Empty and over-cap row sets.
        await ((Func<Task>)(() => Run("plan_insert_posts", """{"rows":[]}""")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*non-empty array*");
        var overCap = $$"""{"rows":[{{string.Join(",", Enumerable.Repeat("""{"title":"x"}""", 21))}}]}""";
        await ((Func<Task>)(() => Run("plan_insert_posts", overCap)))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*capped at 20 rows*");
        // Update without its primary key, and update that changes nothing.
        await ((Func<Task>)(() => Run("plan_update_posts", """{"rows":[{"title":"x"}]}""")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*must include the primary key*");
        await ((Func<Task>)(() => Run("plan_update_posts", """{"rows":[{"id":1}]}""")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*at least one column to change*");
        // Delete rows carry the primary key ONLY.
        await ((Func<Task>)(() => Run("plan_delete_posts", """{"rows":[{"id":1,"title":"x"}]}""")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*name only the primary key*");
        // Non-scalar values and malformed shapes.
        await ((Func<Task>)(() => Run("plan_insert_posts", """{"rows":[{"title":["x"]}]}""")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*JSON scalar*");
        await ((Func<Task>)(() => Run("plan_insert_posts", """{"lines":[]}""")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*Unknown argument 'lines'*");
        await ((Func<Task>)(() => Run("plan_insert_posts", "[]")))
            .Should().ThrowAsync<ChatToolInputException>().WithMessage("*JSON object*");

        // Nothing above wrote or parked anything.
        (await CountAsync("posts")).Should().Be(0);
        _registry.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Execute_WithoutAConversationBinding_IsAConfigurationFault()
    {
        // A transport that cannot bind the conversation cannot host plan tools:
        // loud config fault, never an unbound (cross-conversation) confirmation.
        var caller = new Dictionary<string, object?> { ["tenant_id"] = 1, ["user_id"] = "user-1" };

        var act = () => _connector.ExecuteAsync(
            "plan_insert_posts", TwoPostRows, caller, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{ChatPlanConfirmationRegistry.ConversationContextKey}*");
    }

    // ---- the full tool loop: park between provider turns --------------------------------

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
    public async Task FullToolLoop_ParksOnTheProposal_ConfirmResumes_ApprovedResultFedBack()
    {
        // Arrange: real registry + real connector; only the Anthropic SDK boundary is
        // scripted. Turn 1 calls the plan tool; turn 2 is the final answer.
        var model = await _reads.GetModelAsync(EndpointPath);
        var toolSet = new ChatConnectorRegistry(new IChatConnector[] { _connector }).BuildToolSet(model);
        var turns = new Queue<IAsyncEnumerable<RawMessageStreamEvent>>(new[]
        {
            Stream(
                Start(),
                ToolUseStart("toolu_1", "plan_insert_posts"),
                InputJsonDeltaEvent("""{"rows":[{"title":"Launch note","status":"scheduled"}]}"""),
                FinalDelta("tool_use")),
            Stream(Start(), TextDeltaEvent("Scheduled it."), FinalDelta("end_turn")),
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
                Executor = toolSet.CreateExecutor(Caller(1)),
            },
        };

        // Act: the loop yields the confirmation event and PARKS. The consumer plays
        // the user and approves — between provider turns, no SDK stream is open.
        var events = new List<ChatCompletionEvent>();
        await foreach (var evt in service.StreamAsync(
            new[] { new ChatCompletionMessage(ChatMessageRoles.User, "Schedule the launch note") }, options))
        {
            events.Add(evt);
            if (evt is ChatToolConfirmationActivity confirmation)
            {
                requests.Should().HaveCount(1, "the proposal parks BETWEEN turns — turn 2 must not have started");
                (await CountAsync("posts")).Should().Be(0, "nothing is written while parked");
                _registry.TryResolve(
                    confirmation.Request.ConfirmationId, IdentityOf(1), Conversation,
                    new ChatPlanDecision(true, null)).Should().BeTrue();
            }
        }

        // Assert: event order pins the confirmation choreography.
        events.OfType<ChatToolConfirmationActivity>().Should().ContainSingle()
            .Which.Request.Table.Should().Be("posts");
        var decision = events.OfType<ChatToolConfirmationDecisionActivity>().Should().ContainSingle().Which;
        decision.Approved.Should().BeTrue();
        decision.Operation.Should().Be("insert");
        var resultActivity = events.OfType<ChatToolActivity>().Single(a => a.Phase == ChatToolPhase.Result);
        resultActivity.Summary.Should().Contain("\"approved\":true");

        // The row landed and the model received the approved result verbatim.
        (await CountAsync("posts", "title = 'Launch note' AND tenant_id = 1")).Should().Be(1);
        requests.Should().HaveCount(2);
        requests[1].Messages[2].Content.TryPickContentBlockParams(out var blocks).Should().BeTrue();
        blocks![0].TryPickToolResult(out var toolResult).Should().BeTrue();
        toolResult!.IsError.Should().NotBeTrue();
        toolResult.Content!.TryPickString(out var payload).Should().BeTrue();
        payload.Should().Contain("\"approved\":true");
        events.Last().Should().BeOfType<ChatCompletionResult>()
            .Which.StopReason.Should().Be(ChatCompletionStopReason.Complete);
    }

    [Fact]
    public async Task FullToolLoop_Deny_FeedsTheDeclinedResult_AndTheModelContinues()
    {
        var model = await _reads.GetModelAsync(EndpointPath);
        var toolSet = new ChatConnectorRegistry(new IChatConnector[] { _connector }).BuildToolSet(model);
        var turns = new Queue<IAsyncEnumerable<RawMessageStreamEvent>>(new[]
        {
            Stream(
                Start(),
                ToolUseStart("toolu_1", "plan_delete_posts"),
                InputJsonDeltaEvent("""{"rows":[{"id":1}]}"""),
                FinalDelta("tool_use")),
            Stream(Start(), TextDeltaEvent("Understood, leaving it."), FinalDelta("end_turn")),
        });
        await Exec("INSERT INTO posts(id, tenant_id, title) VALUES (1, 1, 'Keep me')");
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
                Executor = toolSet.CreateExecutor(Caller(1)),
            },
        };

        var events = new List<ChatCompletionEvent>();
        await foreach (var evt in service.StreamAsync(
            new[] { new ChatCompletionMessage(ChatMessageRoles.User, "Delete post 1") }, options))
        {
            events.Add(evt);
            if (evt is ChatToolConfirmationActivity confirmation)
                _registry.TryResolve(
                    confirmation.Request.ConfirmationId, IdentityOf(1), Conversation,
                    new ChatPlanDecision(false, "keep that post")).Should().BeTrue();
        }

        events.OfType<ChatToolConfirmationDecisionActivity>().Should().ContainSingle()
            .Which.Approved.Should().BeFalse();
        (await CountAsync("posts", "id = 1")).Should().Be(1, "denied means nothing is deleted");
        requests[1].Messages[2].Content.TryPickContentBlockParams(out var blocks).Should().BeTrue();
        blocks![0].TryPickToolResult(out var toolResult).Should().BeTrue();
        toolResult!.IsError.Should().NotBeTrue("a denial is not a tool error");
        toolResult.Content!.TryPickString(out var payload).Should().BeTrue();
        payload.Should().Contain("\"approved\":false").And.Contain("keep that post");
        events.Last().Should().BeOfType<ChatCompletionResult>()
            .Which.FullText.Should().Be("Understood, leaving it.");
    }

    // ---- options validation --------------------------------------------------------------

    [Fact]
    public void ChatConnectorOptions_RejectInvalidPlanSettings_FailFast()
    {
        var rowCap = () => new ChatConnectorOptions { PlanRowCap = 0 };
        var timeout = () => new ChatConnectorOptions { PlanConfirmationTimeout = TimeSpan.Zero };

        rowCap.Should().Throw<ArgumentOutOfRangeException>();
        timeout.Should().Throw<ArgumentOutOfRangeException>();
        new ChatConnectorOptions().PlanRowCap.Should().Be(20);
        new ChatConnectorOptions().PlanConfirmationTimeout.Should().Be(TimeSpan.FromMinutes(5));
    }
}
