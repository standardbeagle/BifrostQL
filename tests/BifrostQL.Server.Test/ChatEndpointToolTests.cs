using System.Runtime.CompilerServices;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Chat;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Connector slice 2 at the HTTP seam: the chat middleware builds the completion's
    /// tool options from the DI-registered connectors (executor bound to the CALLER's
    /// auth context), relays <see cref="ChatToolActivity"/> events as SSE <c>tool</c>
    /// events in stream order, maps the tool-loop iteration cap to the typed
    /// <c>error {code:"tool-loop-limit"}</c>, and keeps the persistence contract
    /// unchanged — the assistant row is the final text only, never the tool transcript.
    /// </summary>
    public class ChatEndpointToolTests : IAsyncLifetime
    {
        private readonly ChatEndpointHost _h = new();

        public Task InitializeAsync() => _h.InitializeAsync();

        public async Task DisposeAsync() => await _h.DisposeAsync();

        private static async IAsyncEnumerable<ChatCompletionEvent> Events(
            ChatCompletionEvent[] events, [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var evt in events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return evt;
            }
        }

        /// <summary>
        /// One-tool fake connector recording every execution's auth context. Named
        /// <c>fake_*</c> because the built-in <see cref="ExploreChatConnector"/> is
        /// registered by default and already owns the <c>explore_*</c> names.
        /// </summary>
        private sealed class FakeExploreConnector : IChatConnector
        {
            public int Priority => 200;

            public List<IDictionary<string, object?>> AuthContexts { get; } = new();

            public IReadOnlyList<ChatToolDefinition> GetToolDefinitions(IDbModel model, ChatConnectorBinding binding)
                => new[]
                {
                    new ChatToolDefinition(
                        $"fake_{binding.Table.DbName}",
                        $"Query the {binding.Table.DbName} table when the user asks about its rows.",
                        """{"type":"object","properties":{}}"""),
                };

            public Task<ChatToolResult> ExecuteAsync(
                string toolName, string inputJson, IDictionary<string, object?> authContext,
                CancellationToken cancellationToken)
            {
                AuthContexts.Add(authContext);
                return Task.FromResult(new ChatToolResult { TextPayload = """{"rows":[]}""" });
            }
        }

        [Fact]
        public async Task Sse_relays_tool_events_interleaved_in_stream_order()
        {
            // Arrange: the completion service reports a tool round-trip between two
            // text deltas; the middleware must relay it verbatim, in order.
            var client = await _h.StartAsync();
            _h.Fake.Script = (_, ct) => Events(new ChatCompletionEvent[]
            {
                new ChatCompletionDelta("Let me check. "),
                new ChatToolActivity("explore_messages", ChatToolPhase.Call, """{"filter":"x"}"""),
                new ChatToolActivity("explore_messages", ChatToolPhase.Result, """{"rows":[]}"""),
                new ChatCompletionDelta("Two rows."),
                new ChatCompletionResult("Let me check. Two rows.", ChatCompletionStopReason.Complete, null, 3, 4),
            }, ct);
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");

            // Act
            using var response = await _h.PostMessageAsync(client, conversationId, "What matches?");
            var events = SseReader.Parse(await response.Content.ReadAsStringAsync());

            // Assert: exact event order — the tool transcript rides the stream.
            events.Select(e => e.Name).Should().Equal(
                "message-accepted", "delta", "tool", "tool", "delta", "done");
            events[2].Data.GetProperty("name").GetString().Should().Be("explore_messages");
            events[2].Data.GetProperty("phase").GetString().Should().Be("call");
            events[2].Data.GetProperty("summary").GetString().Should().Be("""{"filter":"x"}""");
            events[3].Data.GetProperty("phase").GetString().Should().Be("result");
            events[3].Data.GetProperty("summary").GetString().Should().Be("""{"rows":[]}""");

            // Persistence contract unchanged: final text only, no tool transcript.
            (await _h.ScalarAsync("SELECT content FROM messages WHERE role = 'assistant'"))
                .Should().Be("Let me check. Two rows.");
        }

        [Fact]
        public async Task Tool_loop_limit_maps_to_the_typed_error_event_and_persists_no_assistant_row()
        {
            var client = await _h.StartAsync();
            _h.Fake.Script = (_, _) => Throwing(new ChatCompletionDelta("Working on it. "),
                new ChatToolLoopLimitException(8));
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");

            using var response = await _h.PostMessageAsync(client, conversationId, "Loop forever");
            var events = SseReader.Parse(await response.Content.ReadAsStringAsync());

            events.Select(e => e.Name).Should().Equal("message-accepted", "delta", "error");
            events[2].Data.GetProperty("code").GetString().Should().Be("tool-loop-limit");
            events[2].Data.GetProperty("message").GetString().Should().Contain("8");

            // Typed failure: the user message stays, no assistant row is written.
            (await _h.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 'assistant'")).Should().Be(0L);
            (await _h.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 'user'")).Should().Be(1L);
        }

        [Fact]
        public async Task Middleware_builds_tool_options_from_registered_connectors_bound_to_the_caller()
        {
            // Arrange: the messages table is a connector and one connector is
            // registered — the completion request must carry its tool with the
            // executor bound to THIS caller's auth context.
            var connector = new FakeExploreConnector();
            var client = await _h.StartAsync(
                connectors: new IChatConnector[] { connector }, messagesAsExploreConnector: true);
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");

            // Act
            using var response = await _h.PostMessageAsync(client, conversationId, "Hello");
            SseReader.Parse(await response.Content.ReadAsStringAsync())
                .Select(e => e.Name).Should().EndWith("done");

            // Assert: the request carried the fake connector's tool — alongside the
            // built-in explore connector's, which registers by default — and the cap.
            var options = _h.Fake.OptionsCalls.Should().ContainSingle().Which;
            var tools = options!.Tools.Should().NotBeNull().And.BeOfType<ChatCompletionToolOptions>().Which;
            tools.MaxToolIterations.Should().Be(8);
            tools.Tools.Select(t => t.Name).Should().BeEquivalentTo("explore_messages", "fake_messages");

            // The executor runs under the caller's identity — invoking it reaches the
            // connector with the request's own auth context, never an ambient one.
            await tools.Executor.ExecuteAsync("fake_messages", "{}", CancellationToken.None);
            var authContext = connector.AuthContexts.Should().ContainSingle().Which;
            authContext.Should().ContainKey("user");
        }

        [Fact]
        public async Task No_connector_tables_means_no_tool_options_on_the_request()
        {
            var connector = new FakeExploreConnector();
            var client = await _h.StartAsync(connectors: new IChatConnector[] { connector });
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");

            using var response = await _h.PostMessageAsync(client, conversationId, "Hello");
            (await response.Content.ReadAsStringAsync()).Should().Contain("done");

            _h.Fake.OptionsCalls.Should().ContainSingle().Which.Should().BeNull();
        }

        private static async IAsyncEnumerable<ChatCompletionEvent> Throwing(
            ChatCompletionEvent first, Exception exception, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return first;
            await Task.Yield();
            throw exception;
        }
    }
}
