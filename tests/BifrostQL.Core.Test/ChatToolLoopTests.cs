using Anthropic.Models.Messages;
using Anthropic.Services;
using BifrostQL.Core.Modules.Chat;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for connector slice 2 — the multi-turn tool-use loop inside
/// <see cref="AnthropicChatCompletionService"/>. The SDK's <see cref="IMessageService"/>
/// is the only fake; each model turn is a scripted stream of literal SSE wire payloads
/// deserialized through the SDK's own converters. Pinned here: the tools request shape,
/// the tool_use turn replay (ONE user message carrying ALL tool_result blocks — the
/// Anthropic contract — and thinking blocks preserved verbatim), tool-activity event
/// order, is_error feedback on connector throws, and the typed iteration-cap error.
/// </summary>
public class ChatToolLoopTests
{
    private static readonly ChatCompletionMessage[] UserOnlyHistory =
        { new(ChatMessageRoles.User, "Hello") };

    private static readonly ChatToolDefinition ExploreTool = new(
        "explore_documents",
        "Query the documents table. Call this whenever the user asks about documents.",
        """{"type":"object","properties":{"filter":{"type":"string"}},"required":[]}""");

    // ----- SSE fixture plumbing (same approach as ChatCompletionServiceTests) -----

    private static RawMessageStreamEvent Event(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<RawMessageStreamEvent>(json)!;

    private static RawMessageStreamEvent Start(long inputTokens) =>
        Event($$"""
            {"type":"message_start","message":{"id":"msg_test","type":"message","role":"assistant",
             "model":"claude-opus-4-8","content":[],"stop_reason":null,"stop_sequence":null,
             "usage":{"input_tokens":{{inputTokens}},"output_tokens":0} } }
            """);

    private static RawMessageStreamEvent TextDeltaEvent(string text, long index = 0) =>
        Event($$"""{"type":"content_block_delta","index":{{index}},"delta":{"type":"text_delta","text":"{{text}}"} }""");

    private static RawMessageStreamEvent ThinkingStart(long index) =>
        Event($$"""{"type":"content_block_start","index":{{index}},"content_block":{"type":"thinking","thinking":"","signature":""} }""");

    private static RawMessageStreamEvent ThinkingDeltaEvent(string thinking, long index) =>
        Event($$"""{"type":"content_block_delta","index":{{index}},"delta":{"type":"thinking_delta","thinking":"{{thinking}}"} }""");

    private static RawMessageStreamEvent SignatureDeltaEvent(string signature, long index) =>
        Event($$"""{"type":"content_block_delta","index":{{index}},"delta":{"type":"signature_delta","signature":"{{signature}}"} }""");

    private static RawMessageStreamEvent ToolUseStart(long index, string id, string name) =>
        Event($$"""{"type":"content_block_start","index":{{index}},"content_block":{"type":"tool_use","id":"{{id}}","name":"{{name}}","input":{} } }""");

    private static RawMessageStreamEvent InputJsonDeltaEvent(string partialJson, long index) =>
        Event(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "content_block_delta",
            ["index"] = index,
            ["delta"] = new Dictionary<string, object?> { ["type"] = "input_json_delta", ["partial_json"] = partialJson },
        }));

    private static RawMessageStreamEvent BlockStop(long index) =>
        Event($$"""{"type":"content_block_stop","index":{{index}} }""");

    private static RawMessageStreamEvent FinalDelta(string stopReason, long outputTokens) =>
        Event($$"""
            {"type":"message_delta","delta":{"stop_reason":"{{stopReason}}","stop_sequence":null},
             "usage":{"output_tokens":{{outputTokens}} } }
            """);

    private static async IAsyncEnumerable<RawMessageStreamEvent> Stream(params RawMessageStreamEvent[] events)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            yield return e;
        }
    }

    /// <summary>
    /// Fakes the SDK boundary with one scripted stream per model turn, capturing each
    /// request as issued — the request-shape pinning seam. A turn beyond the script is
    /// a test bug and fails loudly.
    /// </summary>
    private static (AnthropicChatCompletionService Service, List<MessageCreateParams> Requests) ServiceOverTurns(
        params IAsyncEnumerable<RawMessageStreamEvent>[] turnStreams)
    {
        var requests = new List<MessageCreateParams>();
        var messages = Substitute.For<IMessageService>();
        messages.CreateStreaming(Arg.Any<MessageCreateParams>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                requests.Add(call.Arg<MessageCreateParams>());
                return requests.Count <= turnStreams.Length
                    ? turnStreams[requests.Count - 1]
                    : throw new InvalidOperationException("The test scripted no stream for this model turn.");
            });
        var service = new AnthropicChatCompletionService(messages, new ChatCompletionOptions { ApiKey = "sk-test" });
        return (service, requests);
    }

    /// <summary>Scripted per-tool-name executor recording every call.</summary>
    private sealed class FakeToolExecutor : IChatToolExecutor
    {
        public Dictionary<string, Func<string, ChatToolResult>> Handlers { get; } = new()
        {
            ["explore_documents"] = _ => new ChatToolResult { TextPayload = """{"rows":[]}""" },
        };

        public List<(string ToolName, string InputJson)> Calls { get; } = new();

        public Task<ChatToolResult> ExecuteAsync(string toolName, string inputJson, CancellationToken cancellationToken)
        {
            lock (Calls)
                Calls.Add((toolName, inputJson));
            return Task.FromResult(Handlers[toolName](inputJson));
        }
    }

    private static ChatCompletionRequestOptions ToolOptions(
        FakeToolExecutor executor, int maxIterations = ChatCompletionToolOptions.DefaultMaxToolIterations,
        params ChatToolDefinition[] tools) => new()
    {
        Tools = new ChatCompletionToolOptions
        {
            Tools = tools.Length == 0 ? new[] { ExploreTool } : tools,
            Executor = executor,
            MaxToolIterations = maxIterations,
        },
    };

    private static async Task<List<ChatCompletionEvent>> DrainAll(
        IChatCompletionService service,
        IReadOnlyList<ChatCompletionMessage> history,
        ChatCompletionRequestOptions options)
    {
        var events = new List<ChatCompletionEvent>();
        await foreach (var evt in service.StreamAsync(history, options))
            events.Add(evt);
        return events;
    }

    // ----- the loop: execute, feed back, continue -----

    [Fact]
    public async Task Tool_use_turn_executes_tool_feeds_result_and_continues_to_end_turn()
    {
        // Arrange: turn 1 — text, then a tool call; turn 2 — the final answer.
        var (service, requests) = ServiceOverTurns(
            Stream(
                Start(inputTokens: 10),
                TextDeltaEvent("Let me check. "),
                ToolUseStart(1, "toolu_1", "explore_documents"),
                InputJsonDeltaEvent("{\"filter\":", 1),
                InputJsonDeltaEvent("\"contracts\"}", 1),
                BlockStop(1),
                FinalDelta("tool_use", outputTokens: 20)),
            Stream(
                Start(inputTokens: 30),
                TextDeltaEvent("Two documents match."),
                FinalDelta("end_turn", outputTokens: 5)));
        var executor = new FakeToolExecutor();

        // Act
        var events = await DrainAll(service, UserOnlyHistory, ToolOptions(executor));

        // Assert — event ORDER is the contract transports relay:
        events.Should().HaveCount(5);
        events[0].Should().Be(new ChatCompletionDelta("Let me check. "));
        events[1].Should().Be(new ChatToolActivity(
            "explore_documents", ChatToolPhase.Call, """{"filter":"contracts"}"""));
        events[2].Should().Be(new ChatToolActivity(
            "explore_documents", ChatToolPhase.Result, """{"rows":[]}"""));
        events[3].Should().Be(new ChatCompletionDelta("Two documents match."));
        var result = events[4].Should().BeOfType<ChatCompletionResult>().Which;
        result.StopReason.Should().Be(ChatCompletionStopReason.Complete);
        result.FullText.Should().Be("Let me check. Two documents match.");
        result.InputTokens.Should().Be(40, "prompt tokens sum over both model turns");
        result.OutputTokens.Should().Be(25);

        // The tool executed once with the assembled streamed input.
        executor.Calls.Should().ContainSingle();
        executor.Calls[0].Should().Be(("explore_documents", """{"filter":"contracts"}"""));
        requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Continuation_request_carries_the_assistant_turn_and_one_tool_result_user_message()
    {
        var (service, requests) = ServiceOverTurns(
            Stream(
                Start(1),
                TextDeltaEvent("Checking. "),
                ToolUseStart(1, "toolu_1", "explore_documents"),
                InputJsonDeltaEvent("""{"filter":"x"}""", 1),
                BlockStop(1),
                FinalDelta("tool_use", 1)),
            Stream(Start(1), TextDeltaEvent("Done."), FinalDelta("end_turn", 1)));

        await DrainAll(service, UserOnlyHistory, ToolOptions(new FakeToolExecutor()));

        // Request 2 = [user Hello, assistant(text + tool_use), user(tool_result)].
        var continuation = requests[1].Messages;
        continuation.Should().HaveCount(3);
        continuation.Select(m => m.Role.Raw()).Should().Equal("user", "assistant", "user");

        continuation[1].Content.TryPickContentBlockParams(out var assistantBlocks).Should().BeTrue();
        assistantBlocks.Should().HaveCount(2);
        assistantBlocks[0].TryPickText(out var textBlock).Should().BeTrue();
        textBlock!.Text.Should().Be("Checking. ");
        assistantBlocks[1].TryPickToolUse(out var toolUse).Should().BeTrue();
        toolUse!.ID.Should().Be("toolu_1");
        toolUse.Name.Should().Be("explore_documents");
        toolUse.Input.Should().ContainKey("filter").WhoseValue.GetString().Should().Be("x");

        continuation[2].Content.TryPickContentBlockParams(out var resultBlocks).Should().BeTrue();
        resultBlocks.Should().HaveCount(1);
        resultBlocks[0].TryPickToolResult(out var toolResult).Should().BeTrue();
        toolResult!.ToolUseID.Should().Be("toolu_1");
        toolResult.Content!.TryPickString(out var payload).Should().BeTrue();
        payload.Should().Be("""{"rows":[]}""");
        toolResult.IsError.Should().NotBeTrue();

        // The first request is NOT retroactively mutated by the loop's appends.
        requests[0].Messages.Should().HaveCount(1);
    }

    [Fact]
    public async Task Thinking_blocks_are_replayed_verbatim_in_the_assistant_turn()
    {
        // The API rejects a tool continuation that drops the turn's thinking blocks;
        // signature and content must go back exactly as streamed.
        var (service, requests) = ServiceOverTurns(
            Stream(
                Start(1),
                ThinkingStart(0),
                ThinkingDeltaEvent("pondering", 0),
                SignatureDeltaEvent("sig==", 0),
                BlockStop(0),
                ToolUseStart(1, "toolu_1", "explore_documents"),
                BlockStop(1),
                FinalDelta("tool_use", 1)),
            Stream(Start(1), TextDeltaEvent("Done."), FinalDelta("end_turn", 1)));

        var events = await DrainAll(service, UserOnlyHistory, ToolOptions(new FakeToolExecutor()));

        events.OfType<ChatCompletionDelta>().Select(d => d.Text)
            .Should().Equal(new[] { "Done." }, "thinking is never surfaced as completion text");

        requests[1].Messages[1].Content.TryPickContentBlockParams(out var blocks).Should().BeTrue();
        blocks.Should().HaveCount(2);
        blocks[0].TryPickThinking(out var thinking).Should().BeTrue();
        thinking!.Thinking.Should().Be("pondering");
        thinking.Signature.Should().Be("sig==");
        blocks[1].TryPickToolUse(out var toolUse).Should().BeTrue();
        toolUse!.Input.Should().BeEmpty("a call with no streamed input is an empty argument object");
    }

    // ----- parallel tool calls -----

    [Fact]
    public async Task Parallel_tool_calls_return_all_results_in_one_user_message_in_block_order()
    {
        var orders = new ChatToolDefinition(
            "explore_orders", "Query the orders table when the user asks about orders.",
            """{"type":"object","properties":{}}""");
        var (service, requests) = ServiceOverTurns(
            Stream(
                Start(1),
                ToolUseStart(0, "toolu_a", "explore_documents"),
                InputJsonDeltaEvent("""{"filter":"a"}""", 0),
                BlockStop(0),
                ToolUseStart(1, "toolu_b", "explore_orders"),
                InputJsonDeltaEvent("""{"filter":"b"}""", 1),
                BlockStop(1),
                FinalDelta("tool_use", 1)),
            Stream(Start(1), TextDeltaEvent("Done."), FinalDelta("end_turn", 1)));
        var executor = new FakeToolExecutor();
        executor.Handlers["explore_orders"] = _ => new ChatToolResult { TextPayload = """{"orders":1}""" };

        var events = await DrainAll(service, UserOnlyHistory,
            ToolOptions(executor, tools: new[] { ExploreTool, orders }));

        // Activity order: both calls (block order), then both results (block order).
        events.OfType<ChatToolActivity>().Select(a => (a.ToolName, a.Phase)).Should().Equal(
            ("explore_documents", ChatToolPhase.Call),
            ("explore_orders", ChatToolPhase.Call),
            ("explore_documents", ChatToolPhase.Result),
            ("explore_orders", ChatToolPhase.Result));

        executor.Calls.Should().HaveCount(2);

        // ONE user message carrying ALL tool_result blocks — the Anthropic contract.
        var continuation = requests[1].Messages;
        continuation.Select(m => m.Role.Raw()).Should().Equal("user", "assistant", "user");
        continuation[2].Content.TryPickContentBlockParams(out var resultBlocks).Should().BeTrue();
        resultBlocks.Should().HaveCount(2);
        resultBlocks[0].TryPickToolResult(out var first).Should().BeTrue();
        first!.ToolUseID.Should().Be("toolu_a");
        resultBlocks[1].TryPickToolResult(out var second).Should().BeTrue();
        second!.ToolUseID.Should().Be("toolu_b");
        second.Content!.TryPickString(out var payload).Should().BeTrue();
        payload.Should().Be("""{"orders":1}""");
    }

    // ----- connector failure: is_error feedback, never a crashed stream -----

    [Fact]
    public async Task Connector_throw_feeds_an_is_error_tool_result_back_and_the_loop_continues()
    {
        var (service, requests) = ServiceOverTurns(
            Stream(
                Start(1),
                ToolUseStart(0, "toolu_1", "explore_documents"),
                BlockStop(0),
                FinalDelta("tool_use", 1)),
            Stream(Start(1), TextDeltaEvent("I could not read documents."), FinalDelta("end_turn", 1)));
        var executor = new FakeToolExecutor();
        executor.Handlers["explore_documents"] = _ => throw new InvalidOperationException("documents table unavailable");

        var events = await DrainAll(service, UserOnlyHistory, ToolOptions(executor));

        // The stream survives: activity reports the error, the loop continues to a
        // terminal result.
        events.OfType<ChatToolActivity>().Should().Equal(
            new ChatToolActivity("explore_documents", ChatToolPhase.Call, "{}"),
            new ChatToolActivity("explore_documents", ChatToolPhase.Result, "error: documents table unavailable"));
        events.Last().Should().BeOfType<ChatCompletionResult>()
            .Which.StopReason.Should().Be(ChatCompletionStopReason.Complete);

        // The model saw the failure as an is_error tool_result.
        requests[1].Messages[2].Content.TryPickContentBlockParams(out var blocks).Should().BeTrue();
        blocks![0].TryPickToolResult(out var toolResult).Should().BeTrue();
        toolResult!.IsError.Should().BeTrue();
        toolResult.Content!.TryPickString(out var payload).Should().BeTrue();
        payload.Should().Be("documents table unavailable");
    }

    // ----- iteration cap -----

    [Fact]
    public async Task Exceeding_the_iteration_cap_raises_the_typed_tool_loop_error()
    {
        // Arrange: every turn asks for another tool call; cap is 2 model turns.
        IAsyncEnumerable<RawMessageStreamEvent> ToolTurn(string id) => Stream(
            Start(1),
            ToolUseStart(0, id, "explore_documents"),
            BlockStop(0),
            FinalDelta("tool_use", 1));
        var (service, requests) = ServiceOverTurns(ToolTurn("toolu_1"), ToolTurn("toolu_2"));
        var executor = new FakeToolExecutor();

        var act = () => DrainAll(service, UserOnlyHistory, ToolOptions(executor, maxIterations: 2));

        var ex = (await act.Should().ThrowAsync<ChatToolLoopLimitException>()).Which;
        ex.MaxToolIterations.Should().Be(2);
        ex.Retryable.Should().BeFalse();
        requests.Should().HaveCount(2, "the cap rejects a third provider call, it does not make one");
        executor.Calls.Should().HaveCount(2);
    }

    // ----- request shape -----

    [Fact]
    public async Task Request_carries_the_tool_definitions_with_name_description_and_input_schema()
    {
        var (service, requests) = ServiceOverTurns(
            Stream(Start(1), TextDeltaEvent("hi"), FinalDelta("end_turn", 1)));

        await DrainAll(service, UserOnlyHistory, ToolOptions(new FakeToolExecutor()));

        var tools = requests[0].Tools;
        tools.Should().NotBeNull().And.HaveCount(1);
        tools![0].TryPickTool(out var tool).Should().BeTrue();
        tool!.Name.Should().Be("explore_documents");
        tool.Description.Should().Contain("Call this whenever the user asks about documents");
        tool.InputSchema.Properties.Should().ContainKey("filter");
    }

    [Fact]
    public async Task Request_without_tool_options_carries_no_tools_param()
    {
        var (service, requests) = ServiceOverTurns(
            Stream(Start(1), TextDeltaEvent("hi"), FinalDelta("end_turn", 1)));

        await DrainAll(service, UserOnlyHistory, new ChatCompletionRequestOptions());

        requests[0].Tools.Should().BeNull();
    }

    [Fact]
    public async Task Tool_use_stop_without_configured_tools_fails_fast()
    {
        // No tools were sent, so a tool_use stop is a provider contract violation,
        // not something to coerce into an outcome bucket.
        var (service, _) = ServiceOverTurns(
            Stream(Start(1), FinalDelta("tool_use", 1)));

        var act = () => DrainAll(service, UserOnlyHistory, new ChatCompletionRequestOptions());

        (await act.Should().ThrowAsync<ChatCompletionException>())
            .Which.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task Empty_tool_list_is_rejected_at_the_call_site()
    {
        var (service, _) = ServiceOverTurns();
        var options = new ChatCompletionRequestOptions
        {
            Tools = new ChatCompletionToolOptions
            {
                Tools = Array.Empty<ChatToolDefinition>(),
                Executor = new FakeToolExecutor(),
            },
        };

        var act = () => DrainAll(service, UserOnlyHistory, options);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Invalid_tool_input_schema_is_rejected_at_the_call_site()
    {
        var (service, _) = ServiceOverTurns();
        var options = new ChatCompletionRequestOptions
        {
            Tools = new ChatCompletionToolOptions
            {
                Tools = new[] { new ChatToolDefinition("bad_tool", "A tool.", "not json") },
                Executor = new FakeToolExecutor(),
            },
        };

        var act = () => DrainAll(service, UserOnlyHistory, options);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*bad_tool*");
    }
}
