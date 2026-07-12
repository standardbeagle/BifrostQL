using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Chat;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.Modules.History;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// End-to-end proof of the chat persistence layer (<see cref="ChatConversationStore"/>)
/// over the intent executors: every read runs the filter-transformer pipeline and every
/// write the mutation-transformer chain, so tenant isolation, field encryption, and the
/// change-history trail hold BY CONSTRUCTION — the store has no SQL of its own. The
/// fixtures compose tenant-filter + encrypt + history on the same chat pair, modeled on
/// HistoryTrailShapeTests.
/// </summary>
public sealed class ChatConversationStoreTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_chat_store_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private const string KeyRef = "config:chat";
    private const string ReaderRole = "chat-reader";

    private SqliteConnection _keepAlive = null!;
    private EnvelopeKeyManager _keyManager = null!;
    private ServiceProvider _provider = null!;
    private ChatConversationStore _store = null!;

    private static readonly string[] Rules =
    {
        "main.conversations { chat-conversations: enabled; chat-title: title; tenant-filter: tenant_id }",
        "main.messages { chat-messages: enabled; chat-role: role; chat-content: content; " +
            "chat-conversation-fk: conversation_id; chat-created-at: created_at; " +
            "tenant-filter: tenant_id; history: enabled }",
        "main.messages.content { encrypt: aes-256-gcm; key-ref: config:chat; mask: redact; unmask-role: chat-reader }",
        ":root { history-table: main.__history }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        foreach (var drop in new[] { "messages", "conversations", "__history" })
            await Exec($"DROP TABLE IF EXISTS {drop}");

        await Exec(
            """
            CREATE TABLE conversations (
                id        INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                title     TEXT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE messages (
                id              INTEGER PRIMARY KEY,
                tenant_id       INTEGER NOT NULL,
                conversation_id INTEGER NOT NULL REFERENCES conversations(id),
                role            TEXT NOT NULL,
                content         TEXT NULL,
                created_at      DATETIME NULL
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
                tenant_id       INTEGER NULL
            )
            """);

        var root = new byte[FieldCipher.KeySize];
        for (var i = 0; i < root.Length; i++) root[i] = (byte)(i + 7);
        _keyManager = new EnvelopeKeyManager(new ConfigRootKeyProvider(root), new InMemoryDataEncryptionKeyStore());

        // The store's DI surface: key manager for encrypt/decrypt, history hook for
        // the trail — resolved by the pipelines behind the intent executors.
        var services = new ServiceCollection();
        services.AddSingleton(_keyManager);
        services.AddSingleton<HistoryMutationHook>();
        services.AddSingleton<IBeforeCommitMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton<IInTransactionMutationHook>(sp => sp.GetRequiredService<HistoryMutationHook>());
        services.AddSingleton(sp => new BeforeCommitMutationHooks(
            sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
        services.AddSingleton(sp => new InTransactionMutationHooks(
            sp.GetServices<IInTransactionMutationHook>().ToArray()));
        _provider = services.BuildServiceProvider();

        _store = BuildStore(_provider);
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

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    /// <summary>
    /// Builds the store over real intent executors carrying the same built-in
    /// security transformers the server registers: tenant filter + encrypted-column
    /// guard on reads, tenant pinning + encrypt-on-write on mutations.
    /// </summary>
    private static ChatConversationStore BuildStore(IServiceProvider provider, string connString = ConnString)
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(connString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["dbSchema"] = DbSchema.FromModel(model),
                ["connFactory"] = factory,
            });
        });

        var reads = new QueryIntentExecutor(
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
            services: provider);

        var writes = new MutationIntentExecutor(
            pathCache,
            new MutationTransformersWrap
            {
                Transformers = new IMutationTransformer[]
                {
                    new TenantMutationTransformer(),
                    new EncryptOnWriteMutationTransformer(),
                },
            },
            provider);

        return new ChatConversationStore(reads, writes, EndpointPath);
    }

    private static IDictionary<string, object?> Tenant(int tenantId, params string[] roles) =>
        new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId,
            ["roles"] = roles,
        };

    private static IDictionary<string, object?> Unauthenticated() => new Dictionary<string, object?>();

    // ---- tenant isolation --------------------------------------------------

    [Fact]
    public async Task ListConversations_EachTenantSeesOnlyItsOwn_NewestFirst()
    {
        await _store.CreateConversationAsync(Tenant(1), "alpha");
        await _store.CreateConversationAsync(Tenant(1), "beta");
        await _store.CreateConversationAsync(Tenant(2), "gamma");

        var tenantA = await _store.ListConversationsAsync(Tenant(1), new ChatPage(10));
        var tenantB = await _store.ListConversationsAsync(Tenant(2), new ChatPage(10));

        tenantA.TotalCount.Should().Be(2);
        tenantA.Rows.Select(r => (string?)r["title"]).Should().Equal("beta", "alpha");
        tenantB.TotalCount.Should().Be(1);
        tenantB.Rows.Select(r => (string?)r["title"]).Should().Equal("gamma");
    }

    [Fact]
    public async Task PageMessages_NeverCrossesTenants()
    {
        var convA = await _store.CreateConversationAsync(Tenant(1), "a");
        var convB = await _store.CreateConversationAsync(Tenant(2), "b");
        await _store.AppendMessageAsync(Tenant(1), convA!, ChatMessageRoles.User, "a-secret");
        await _store.AppendMessageAsync(Tenant(2), convB!, ChatMessageRoles.User, "b-secret");

        // Tenant B pages tenant A's conversation: the tenant filter yields zero rows.
        var crossRead = await _store.PageMessagesAsync(Tenant(2, ReaderRole), convA!, new ChatPage(10));
        crossRead.Rows.Should().BeEmpty();
        crossRead.TotalCount.Should().Be(0);

        var ownRead = await _store.PageMessagesAsync(Tenant(2, ReaderRole), convB!, new ChatPage(10));
        ownRead.Rows.Should().ContainSingle().Which["content"].Should().Be("b-secret");
    }

    [Fact]
    public async Task UnauthenticatedCaller_FailsClosed_OnEveryOperation()
    {
        // Pin the seam's behavior: the tenant filter transformer throws a typed
        // denial when the claim is missing — no rows, no writes, fail-closed.
        var conv = await _store.CreateConversationAsync(Tenant(1), "private");

        var list = () => _store.ListConversationsAsync(Unauthenticated(), new ChatPage(10));
        await list.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*Tenant context required*");

        var page = () => _store.PageMessagesAsync(Unauthenticated(), conv!, new ChatPage(10));
        await page.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*Tenant context required*");

        var create = () => _store.CreateConversationAsync(Unauthenticated(), "nope");
        await create.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*Tenant context required*");

        // Append probes conversation visibility through the read executor first,
        // so the same typed denial fires before anything is written.
        var append = () => _store.AppendMessageAsync(Unauthenticated(), conv!, ChatMessageRoles.User, "nope");
        await append.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*Tenant context required*");
        (await ScalarAsync("SELECT COUNT(*) FROM messages")).Should().Be("0", "nothing may be written fail-open");
    }

    [Fact]
    public async Task AppendMessage_ToAnotherTenantsConversation_FailsClosedAsNotFound()
    {
        var convA = await _store.CreateConversationAsync(Tenant(1), "a");

        var act = () => _store.AppendMessageAsync(Tenant(2), convA!, ChatMessageRoles.User, "hijack");

        // Cross-tenant and nonexistent are indistinguishable: the visibility probe
        // returns zero rows either way, and the store refuses the write.
        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*not found*");
        var missing = () => _store.AppendMessageAsync(Tenant(2), 999_999L, ChatMessageRoles.User, "ghost");
        await missing.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*not found*");
        (await ScalarAsync("SELECT COUNT(*) FROM messages")).Should().Be("0");
    }

    [Fact]
    public async Task AppendMessage_ConversationDeletedBetweenProbeAndInsert_IsTypedNotFound_NoOrphan_FkEnforced()
    {
        // Race window pinned via the test seam: the conversation is hard-deleted
        // between the visibility probe and the insert. The factory defaults SQLite to
        // Foreign Keys=True, so the insert hits an FK violation — which must surface
        // as the SAME typed not-found as a never-existing conversation, never as a
        // provider DbException, and must leave no message row behind.
        var conv = await _store.CreateConversationAsync(Tenant(1), "doomed");
        _store.AfterConversationVisibilityProbe = () => Exec($"DELETE FROM conversations WHERE id = {conv}");

        var act = () => _store.AppendMessageAsync(Tenant(1), conv!, ChatMessageRoles.User, "into the void");

        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*not found*");
        (await ScalarAsync("SELECT COUNT(*) FROM messages")).Should().Be("0", "no orphaned message may survive the race");
    }

    [Fact]
    public async Task AppendMessage_ConversationDeletedBetweenProbeAndInsert_IsCompensated_WhenFkIsNotEnforced()
    {
        // Same race on an engine/link that does NOT enforce the FK (explicit
        // Foreign Keys=False stands in for metadata-only join rules and legacy
        // SQLite files): the insert succeeds and would silently orphan the message.
        // The post-insert re-probe must remove the orphan and report the same typed
        // not-found. The remaining race — a delete landing after that re-probe — is
        // indistinguishable from a delete after a successful append and is the
        // conversation-delete path's responsibility.
        var store = BuildStore(_provider, ConnString + ";Foreign Keys=False");
        var conv = await store.CreateConversationAsync(Tenant(1), "doomed");
        store.AfterConversationVisibilityProbe = () => Exec($"DELETE FROM conversations WHERE id = {conv}");

        var act = () => store.AppendMessageAsync(Tenant(1), conv!, ChatMessageRoles.User, "into the void");

        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*not found*");
        (await ScalarAsync("SELECT COUNT(*) FROM messages")).Should().Be("0", "the compensating delete removes the orphan");
    }

    // ---- append: role gate, server-side stamp, history, crypto ---------------

    [Fact]
    public async Task AppendMessage_UnknownRole_IsRejected_BeforeAnythingIsWritten()
    {
        var conv = await _store.CreateConversationAsync(Tenant(1), "a");

        var act = () => _store.AppendMessageAsync(Tenant(1), conv!, "narrator", "once upon a time");

        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().Contain("narrator")
            .And.Contain(ChatMessageRoles.User)
            .And.Contain(ChatMessageRoles.Assistant)
            .And.Contain(ChatMessageRoles.System);
        (await ScalarAsync("SELECT COUNT(*) FROM messages")).Should().Be("0");
    }

    [Fact]
    public async Task AppendMessage_StampsCreatedAtServerSide_AndRoundTrips()
    {
        var conv = await _store.CreateConversationAsync(Tenant(1), "a");

        var messageId = await _store.AppendMessageAsync(Tenant(1), conv!, "USER", "hello there");

        messageId.Should().NotBeNull("the insert returns the generated message id");
        (await ScalarAsync($"SELECT created_at FROM messages WHERE id = {messageId}"))
            .Should().NotBeNullOrEmpty("created-at is stamped server-side, never caller-supplied");

        var page = await _store.PageMessagesAsync(Tenant(1, ReaderRole), conv!, new ChatPage(10));
        var row = page.Rows.Should().ContainSingle().Subject;
        row["role"].Should().Be(ChatMessageRoles.User, "the role is stored in canonical lower-case form");
        row["content"].Should().Be("hello there");
        row["created_at"].Should().NotBeNull();
    }

    [Fact]
    public async Task AppendMessage_OnHistoryEnabledMessagesTable_ProducesATrailRow()
    {
        var conv = await _store.CreateConversationAsync(Tenant(1), "a");

        var messageId = await _store.AppendMessageAsync(Tenant(1), conv!, ChatMessageRoles.Assistant, "composed");

        // Composition: the store wrote through the mutation pipeline, so the
        // history hook recorded the insert — one trail row naming the message.
        (await ScalarAsync("SELECT COUNT(*) FROM __history WHERE op = 'insert' AND entity LIKE '%messages'"))
            .Should().Be("1");
        (await ScalarAsync("SELECT entity_id FROM __history")).Should().Contain(messageId!.ToString()!);
    }

    [Fact]
    public async Task EncryptedContent_IsCiphertextAtRest_DecryptsOnAuthorizedRead_MasksOtherwise()
    {
        const string plaintext = "the launch code is 0000";
        var conv = await _store.CreateConversationAsync(Tenant(1), "a");
        await _store.AppendMessageAsync(Tenant(1), conv!, ChatMessageRoles.User, plaintext);

        // At rest: the column holds a decryptable envelope, never the plaintext.
        var stored = await ScalarAsync("SELECT content FROM messages");
        stored.Should().NotBeNull().And.NotBe(plaintext);
        FieldCipher.Decrypt(_keyManager.GetDataKey(KeyRef), stored!, CryptoAad.Build("main", "messages", "content"))
            .Should().Be(plaintext);

        // Authorized read (unmask role): plaintext through the intent seam.
        var reader = await _store.PageMessagesAsync(Tenant(1, ReaderRole), conv!, new ChatPage(10));
        reader.Rows.Should().ContainSingle().Which["content"].Should().Be(plaintext);

        // Same tenant without the unmask role: masked, and never the ciphertext.
        var masked = await _store.PageMessagesAsync(Tenant(1), conv!, new ChatPage(10));
        var value = (string?)masked.Rows.Should().ContainSingle().Subject["content"];
        value.Should().NotBe(plaintext).And.NotBe(stored);
    }

    // ---- typed not-found + recent-history read (SSE endpoint seams) -----------

    [Fact]
    public async Task NotFound_IsTheTypedChatConversationNotFoundException()
    {
        // Transports (the SSE endpoint) map not-found to 404 by exception TYPE,
        // never by string-matching messages — pin the type on both shapes.
        var convA = await _store.CreateConversationAsync(Tenant(1), "a");

        var crossTenant = () => _store.AppendMessageAsync(Tenant(2), convA!, ChatMessageRoles.User, "hijack");
        var missing = () => _store.AppendMessageAsync(Tenant(1), 999_999L, ChatMessageRoles.User, "ghost");

        await crossTenant.Should().ThrowAsync<ChatConversationNotFoundException>();
        await missing.Should().ThrowAsync<ChatConversationNotFoundException>();
    }

    [Fact]
    public async Task IsConversationVisible_TracksRowScope_FailClosed()
    {
        var conv = await _store.CreateConversationAsync(Tenant(1), "a");

        (await _store.IsConversationVisibleAsync(Tenant(1), conv!)).Should().BeTrue();
        (await _store.IsConversationVisibleAsync(Tenant(2), conv!))
            .Should().BeFalse("another tenant's conversation reads as not visible");
        (await _store.IsConversationVisibleAsync(Tenant(1), 999_999L))
            .Should().BeFalse("a nonexistent conversation is indistinguishable from an out-of-scope one");
    }

    [Fact]
    public async Task ListRecentMessages_ReturnsTheLastNChronologically_ShapedForTheCompletionService()
    {
        var conv = await _store.CreateConversationAsync(Tenant(1), "a");
        // Seed through the store so content rides the encrypt-on-write pipeline;
        // same-timestamp rows order by the monotonic primary key (append order).
        await _store.AppendMessageAsync(Tenant(1), conv!, ChatMessageRoles.User, "oldest");
        await _store.AppendMessageAsync(Tenant(1), conv!, ChatMessageRoles.Assistant, "middle");
        await _store.AppendMessageAsync(Tenant(1), conv!, ChatMessageRoles.User, "newer");
        await _store.AppendMessageAsync(Tenant(1), conv!, ChatMessageRoles.Assistant, "newest");

        var bounded = await _store.ListRecentMessagesAsync(Tenant(1, ReaderRole), conv!, limit: 3);
        var unbounded = await _store.ListRecentMessagesAsync(Tenant(1, ReaderRole), conv!, limit: 10);

        // The unmask role decrypts through the read pipeline, so the completion
        // service receives plaintext turns in chronological order, oldest truncated.
        bounded.Select(m => (m.Role, m.Content)).Should().Equal(
            ("assistant", "middle"), ("user", "newer"), ("assistant", "newest"));
        unbounded.Should().HaveCount(4, "a conversation under the window is sent whole");
        unbounded[0].Content.Should().Be("oldest");
    }

    // ---- ordering + paging ---------------------------------------------------

    [Fact]
    public async Task PageMessages_OrdersByCreatedAt_ThenPrimaryKey_Deterministically()
    {
        var conv = await _store.CreateConversationAsync(Tenant(1), "a");

        // Seed directly: two rows share one timestamp (the PK must break the tie)
        // and the row with the LOWEST id carries the LATEST timestamp, so insertion
        // order cannot masquerade as chronological order.
        await Exec($"""
            INSERT INTO messages (id, tenant_id, conversation_id, role, content, created_at) VALUES
                (101, 1, {conv}, 'user',      'late',  '2026-01-01 10:00:05'),
                (102, 1, {conv}, 'assistant', 'first', '2026-01-01 10:00:01'),
                (103, 1, {conv}, 'user',      'tied',  '2026-01-01 10:00:01')
            """);

        var page = await _store.PageMessagesAsync(Tenant(1), conv!, new ChatPage(10));

        page.Rows.Select(r => Convert.ToInt64(r["id"])).Should().Equal(102, 103, 101);

        // Paging preserves the same total order across page boundaries.
        var first = await _store.PageMessagesAsync(Tenant(1), conv!, new ChatPage(2));
        var second = await _store.PageMessagesAsync(Tenant(1), conv!, new ChatPage(2, offset: 2));
        first.Rows.Select(r => Convert.ToInt64(r["id"])).Should().Equal(102, 103);
        second.Rows.Select(r => Convert.ToInt64(r["id"])).Should().Equal(101);
        first.TotalCount.Should().Be(3);
        second.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task ListConversations_PagesNewestFirst_WithScopedTotal()
    {
        foreach (var title in new[] { "one", "two", "three" })
            await _store.CreateConversationAsync(Tenant(1), title);
        await _store.CreateConversationAsync(Tenant(2), "other-tenant");

        var first = await _store.ListConversationsAsync(Tenant(1), new ChatPage(2));
        var second = await _store.ListConversationsAsync(Tenant(1), new ChatPage(2, offset: 2));

        first.Rows.Select(r => (string?)r["title"]).Should().Equal("three", "two");
        second.Rows.Select(r => (string?)r["title"]).Should().Equal("one");
        first.TotalCount.Should().Be(3, "the total counts only the caller's tenant");
    }
}
