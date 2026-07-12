using System.Net;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Chat;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// End-to-end coverage of the chat SSE endpoints (<see cref="BifrostChatMiddleware"/>)
    /// over a real TestServer host: SQLite chat pair with tenant isolation and change
    /// history, real intent executors, real HTTP authentication — only the LLM seam
    /// (<see cref="IChatCompletionService"/>) is a scripted fake, so no network is touched.
    /// </summary>
    public sealed class ChatEndpointTests : IAsyncLifetime
    {
        private readonly ChatEndpointHost _h = new();

        public Task InitializeAsync() => _h.InitializeAsync();

        public async Task DisposeAsync() => await _h.DisposeAsync();

        // ---- happy path ------------------------------------------------------

        [Fact]
        public async Task CreateConversation_ReturnsId_AndPersistsTenantPinnedRow()
        {
            var client = await _h.StartAsync();

            var id = await _h.CreateConversationAsync(client, "tenant-a", "greetings");

            (await _h.ScalarAsync($"SELECT title FROM conversations WHERE id = {id}")).Should().Be("greetings");
            (await _h.ScalarAsync($"SELECT tenant_id FROM conversations WHERE id = {id}"))
                .Should().Be("tenant-a", "the tenant mutation transformer pins the caller's tenant");
        }

        [Fact]
        public async Task PostMessage_StreamsSseSequence_AndPersistsBothRows_WithHistoryTrail()
        {
            var client = await _h.StartAsync();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");
            _h.Fake.Script = Scripts.Deltas(new[] { "Hello", " world" },
                new ChatCompletionResult("Hello world", ChatCompletionStopReason.Complete, null, 10, 2));

            using var response = await _h.PostMessageAsync(client, conversationId, "hi there");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
            var events = SseReader.Parse(await response.Content.ReadAsStringAsync());

            events.Select(e => e.Name).Should().Equal("message-accepted", "delta", "delta", "done");
            events[0].Data.GetProperty("conversationId").GetInt64().Should().Be(conversationId);
            var userMessageId = events[0].Data.GetProperty("userMessageId").GetInt64();
            events[1].Data.GetProperty("text").GetString().Should().Be("Hello");
            events[2].Data.GetProperty("text").GetString().Should().Be(" world");
            events[3].Data.GetProperty("stopReason").GetString().Should().Be("complete");
            var assistantMessageId = events[3].Data.GetProperty("assistantMessageId").GetInt64();

            (await _h.ScalarAsync($"SELECT content FROM messages WHERE id = {userMessageId} AND role = 'user'"))
                .Should().Be("hi there");
            (await _h.ScalarAsync($"SELECT content FROM messages WHERE id = {assistantMessageId} AND role = 'assistant'"))
                .Should().Be("Hello world");
            (await _h.ScalarAsync("SELECT COUNT(*) FROM __history WHERE op = 'insert' AND entity LIKE '%messages'"))
                .Should().Be(2L, "history metadata records a trail row per persisted message");
        }

        [Fact]
        public async Task Truncated_PersistsAccumulatedText_AndReportsStopReasonInDoneEvent()
        {
            var client = await _h.StartAsync();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");
            _h.Fake.Script = Scripts.Deltas(new[] { "partial answ" },
                new ChatCompletionResult("partial answ", ChatCompletionStopReason.Truncated, null, 10, 2));

            using var response = await _h.PostMessageAsync(client, conversationId, "go");
            var events = SseReader.Parse(await response.Content.ReadAsStringAsync());

            events.Last().Name.Should().Be("done");
            events.Last().Data.GetProperty("stopReason").GetString().Should().Be("truncated");
            (await _h.ScalarAsync("SELECT content FROM messages WHERE role = 'assistant'"))
                .Should().Be("partial answ", "truncated content is persisted as-is; the flag travels only in the done event");
        }

        // ---- refusal / provider error contracts ------------------------------

        [Fact]
        public async Task Refusal_EmitsTypedErrorEvent_AndPersistsNothingForTheAssistant()
        {
            var client = await _h.StartAsync();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");
            _h.Fake.Script = Scripts.Deltas(new[] { "I can start but" },
                new ChatCompletionResult("I can start but", ChatCompletionStopReason.Refused, "safety", 10, 2));

            using var response = await _h.PostMessageAsync(client, conversationId, "do the bad thing");
            var events = SseReader.Parse(await response.Content.ReadAsStringAsync());

            events.Select(e => e.Name).Should().Equal("message-accepted", "delta", "error");
            events.Last().Data.GetProperty("code").GetString().Should().Be("refusal");
            events.Last().Data.GetProperty("refusalCategory").GetString().Should().Be("safety");
            (await _h.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 'assistant'"))
                .Should().Be(0L, "a refusal persists nothing for the assistant, including streamed partial text");
            (await _h.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 'user'"))
                .Should().Be(1L, "the accepted user message stays");
        }

        [Fact]
        public async Task ProviderFailureMidStream_EmitsTypedErrorEvent_AndPersistsNoAssistantRow()
        {
            var client = await _h.StartAsync();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");
            _h.Fake.Script = (_, _) => Scripts.DeltaThenThrow("half an",
                new ChatCompletionException("The provider rate-limited the request.", retryable: true));

            using var response = await _h.PostMessageAsync(client, conversationId, "go");
            var events = SseReader.Parse(await response.Content.ReadAsStringAsync());

            events.Select(e => e.Name).Should().Equal("message-accepted", "delta", "error");
            events.Last().Data.GetProperty("code").GetString().Should().Be("provider-error");
            events.Last().Data.GetProperty("retryable").GetBoolean().Should().BeTrue();
            (await _h.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 'assistant'")).Should().Be(0L);
        }

        // ---- history bounding + system prompt --------------------------------

        [Fact]
        public async Task History_SendsOnlyTheLastNMessagesChronologically_WithConfiguredSystemPromptFirst()
        {
            var client = await _h.StartAsync(historyLimit: 3, systemPrompt: "You are terse.");
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");
            for (var i = 1; i <= 4; i++)
                await _h.ExecAsync(
                    $"INSERT INTO messages (tenant_id, conversation_id, role, content, created_at) VALUES " +
                    $"('tenant-a', {conversationId}, '{(i % 2 == 1 ? "user" : "assistant")}', 'seed-{i}', '2026-01-01 10:00:0{i}')");
            _h.Fake.Script = Scripts.Deltas(new[] { "ok" },
                new ChatCompletionResult("ok", ChatCompletionStopReason.Complete, null, 1, 1));

            using var response = await _h.PostMessageAsync(client, conversationId, "newest");

            SseReader.Parse(await response.Content.ReadAsStringAsync()).Last().Name.Should().Be("done");
            var sent = _h.Fake.Calls.Should().ContainSingle().Subject;
            sent.Select(m => (m.Role, m.Content)).Should().Equal(
                ("system", "You are terse."),
                ("user", "seed-3"),
                ("assistant", "seed-4"),
                ("user", "newest"));
        }

        // ---- fail-closed auth + tenancy ---------------------------------------

        [Fact]
        public async Task Unauthenticated_Is401_BeforeAnyPersistenceOrProviderCall()
        {
            var client = await _h.StartAsync();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");
            _h.Fake.Calls.Clear();

            using var createResponse = await client.SendAsync(ChatEndpointHost.Post(
                "/_chat/conversations", new { title = "nope" }, user: null, tenant: null));
            using var messageResponse = await client.SendAsync(ChatEndpointHost.Post(
                $"/_chat/conversations/{conversationId}/messages", new { content = "nope" }, user: null, tenant: null));

            createResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            messageResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            _h.Fake.Calls.Should().BeEmpty("the provider must never be reached without identity");
            (await _h.ScalarAsync("SELECT COUNT(*) FROM messages")).Should().Be(0L);
            (await _h.ScalarAsync("SELECT COUNT(*) FROM conversations WHERE title = 'nope'")).Should().Be(0L);
        }

        [Fact]
        public async Task AuthenticatedWithoutTenantClaim_FailsClosed_WritesNothing()
        {
            var client = await _h.StartAsync();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");

            using var response = await client.SendAsync(ChatEndpointHost.Post(
                $"/_chat/conversations/{conversationId}/messages", new { content = "no tenant" }, "user-x", tenant: null));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "the tenant transformer's fail-closed denial maps to 403");
            (await _h.ScalarAsync("SELECT COUNT(*) FROM messages")).Should().Be(0L);
            _h.Fake.Calls.Should().BeEmpty();
        }

        [Fact]
        public async Task CrossTenantConversation_Is404_InBothDirections_AndMatchesNonexistent()
        {
            var client = await _h.StartAsync();
            var conversationA = await _h.CreateConversationAsync(client, "tenant-a");
            var conversationB = await _h.CreateConversationAsync(client, "tenant-b");

            using var bIntoA = await _h.PostMessageAsync(client, conversationA, "hijack", tenant: "tenant-b");
            using var aIntoB = await _h.PostMessageAsync(client, conversationB, "hijack", tenant: "tenant-a");
            using var ghost = await _h.PostMessageAsync(client, 999_999, "ghost", tenant: "tenant-a");

            bIntoA.StatusCode.Should().Be(HttpStatusCode.NotFound);
            aIntoB.StatusCode.Should().Be(HttpStatusCode.NotFound);
            ghost.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "nonexistent and cross-tenant must be indistinguishable");
            (await _h.ScalarAsync("SELECT COUNT(*) FROM messages")).Should().Be(0L);
            _h.Fake.Calls.Should().BeEmpty();
        }

        // ---- disconnect + concurrency -----------------------------------------

        [Fact]
        public async Task ClientDisconnectMidStream_CancelsTheProvider_AndWritesNoAssistantRow()
        {
            var client = await _h.StartAsync();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _h.Fake.Script = (_, ct) => Scripts.DeltaThenHang("thinking…", cancelled, ct);

            var response = await _h.PostMessageAsync(client, conversationId, "hi", "tenant-a",
                HttpCompletionOption.ResponseHeadersRead);
            var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            (await SseReader.ReadNextAsync(reader)).Name.Should().Be("message-accepted");
            (await SseReader.ReadNextAsync(reader)).Name.Should().Be("delta");

            // Disconnect: closing the response stream aborts the server request
            // (HttpContext.RequestAborted fires), like a dropped connection.
            reader.Dispose();
            response.Dispose();

            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            (await _h.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 'assistant'"))
                .Should().Be(0L, "a disconnect must never persist a partial assistant message");
            (await _h.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 'user'"))
                .Should().Be(1L, "the accepted user message stays");
        }

        [Fact]
        public async Task SecondPostWhileStreaming_Is409_AndPersistsNothing_ThenTheGuardReleases()
        {
            var client = await _h.StartAsync();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _h.Fake.Script = (_, ct) => Scripts.DeltaThenGate("streaming", started, gate, ct);

            var firstTask = _h.PostMessageAsync(client, conversationId, "first");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            using var second = await _h.PostMessageAsync(client, conversationId, "second");
            second.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await _h.ScalarAsync("SELECT COUNT(*) FROM messages WHERE content = 'second'"))
                .Should().Be(0L, "a 409 rejects the request before persisting its user message");

            gate.SetResult();
            using var first = await firstTask;
            SseReader.Parse(await first.Content.ReadAsStringAsync()).Last().Name.Should().Be("done");

            // The guard released in finally: the conversation streams again.
            _h.Fake.Script = Scripts.Deltas(new[] { "again" },
                new ChatCompletionResult("again", ChatCompletionStopReason.Complete, null, 1, 1));
            using var third = await _h.PostMessageAsync(client, conversationId, "third");
            third.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // ---- request validation ------------------------------------------------

        [Fact]
        public async Task InvalidRequests_GetTypedHttpErrors()
        {
            var client = await _h.StartAsync();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");

            using var get = await client.SendAsync(new HttpRequestMessage(
                HttpMethod.Get, "/_chat/conversations"));
            get.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);

            using var blank = await _h.PostMessageAsync(client, conversationId, "   ");
            blank.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var badJson = ChatEndpointHost.Post(
                $"/_chat/conversations/{conversationId}/messages", null, "user-of-tenant-a", "tenant-a");
            badJson.Content = new StringContent("{not json", Encoding.UTF8, "application/json");
            using var malformed = await client.SendAsync(badJson);
            malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            (await _h.ScalarAsync("SELECT COUNT(*) FROM messages")).Should().Be(0L);
        }

        // ---- startup validation --------------------------------------------------

        [Fact]
        public async Task UseBifrostChat_ResolvesTheCompletionServiceAtStartup_SoAMisconfiguredHostFailsToStart()
        {
            var act = () => BuildOptionHostAsync(
                _ => { },
                services => services.AddSingleton<IChatCompletionService>(_ =>
                    // Stands in for AnthropicChatCompletionService's fail-fast
                    // constructor (missing ANTHROPIC_API_KEY).
                    throw new InvalidOperationException("The Anthropic api key is not configured.")));

            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*api key*");
        }

        [Fact]
        public async Task UseBifrostChat_RejectsInvalidOptions_AtStartup()
        {
            Func<Task> badLimit = () => BuildOptionHostAsync(o => o.HistoryLimit = 0);
            Func<Task> badPath = () => BuildOptionHostAsync(o => o.Path = "no-slash");

            (await badLimit.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*HistoryLimit*");
            (await badPath.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Path*");
        }

        private async Task BuildOptionHostAsync(
            Action<BifrostChatOptions> configure, Action<IServiceCollection>? overrideServices = null)
        {
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    if (overrideServices is null)
                        services.AddSingleton<IChatCompletionService>(_h.Fake);
                    else
                        overrideServices(services);
                    services.AddBifrostEndpoints(o => o.AddEndpoint(e =>
                    {
                        e.ConnectionString = "Data Source=chat_options_check;Mode=Memory;Cache=Shared";
                        e.Provider = "sqlite";
                        e.Path = "/graphql";
                        e.DisableAuth = true;
                    }));
                });
                web.Configure(app => app.UseBifrostChat(configure));
            });
            using var host = await builder.StartAsync();
            await host.StopAsync();
        }
    }
}
