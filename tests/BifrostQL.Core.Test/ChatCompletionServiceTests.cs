using System.Net;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Services;
using BifrostQL.Core.Modules.Chat;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for Chat slice 3 — the Anthropic streaming completion service. The SDK's
/// <see cref="IMessageService"/> is the system boundary and the only thing faked; the
/// stream events fed through it are hand-built SDK types (no live API key in CI), so
/// what is pinned here is our mapping: SDK text deltas → <see cref="ChatCompletionDelta"/>,
/// the stop-reason taxonomy → <see cref="ChatCompletionResult"/>, SDK exceptions → the
/// retryable/non-retryable <see cref="ChatCompletionException"/> buckets, and the
/// request shape (adaptive thinking, no sampling params, system role → system prompt).
/// </summary>
public class ChatCompletionServiceTests
{
    private const string Opus48 = "claude-opus-4-8";

    private static readonly ChatCompletionMessage[] UserOnlyHistory =
        { new(ChatMessageRoles.User, "Hello") };

    private static ChatCompletionOptions Options() => new() { ApiKey = "sk-test" };

    // ----- fake-stream plumbing (SDK events built directly; IMessageService faked) -----

    private static async IAsyncEnumerable<RawMessageStreamEvent> Stream(params RawMessageStreamEvent[] events)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            yield return e;
        }
    }

    private static async IAsyncEnumerable<RawMessageStreamEvent> Throwing(Exception exception)
    {
        await Task.Yield();
        if (exception is not null)
            throw exception;
        yield break;
    }

    // Fixtures are literal SSE wire payloads deserialized through the SDK's own
    // converters — the closest thing to a snapshot of the real stream without a
    // live API key (none is available in this environment).
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

    private static RawMessageStreamEvent ThinkingDeltaEvent(string thinking) =>
        Event($$"""{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"{{thinking}}"} }""");

    private static RawMessageStreamEvent FinalDelta(string stopReason, long outputTokens, string? refusalCategory = null)
    {
        var stopDetails = refusalCategory is null
            ? string.Empty
            : $$""","stop_details":{"type":"refusal","category":"{{refusalCategory}}"}""";
        return Event($$"""
            {"type":"message_delta","delta":{"stop_reason":"{{stopReason}}","stop_sequence":null{{stopDetails}}},
             "usage":{"output_tokens":{{outputTokens}} } }
            """);
    }

    private static (AnthropicChatCompletionService Service, Func<MessageCreateParams?> Captured) ServiceOver(
        IAsyncEnumerable<RawMessageStreamEvent> stream)
    {
        MessageCreateParams? captured = null;
        var messages = Substitute.For<IMessageService>();
        messages.CreateStreaming(Arg.Do<MessageCreateParams>(p => captured = p), Arg.Any<CancellationToken>())
            .Returns(stream);
        return (new AnthropicChatCompletionService(messages, Options()), () => captured);
    }

    private static async Task<(List<string> Deltas, ChatCompletionResult Result)> Drain(
        IChatCompletionService service,
        IReadOnlyList<ChatCompletionMessage> history,
        ChatCompletionRequestOptions? options = null)
    {
        var deltas = new List<string>();
        ChatCompletionResult? result = null;
        await foreach (var evt in service.StreamAsync(history, options))
        {
            switch (evt)
            {
                case ChatCompletionDelta delta:
                    result.Should().BeNull("no delta may follow the terminal result");
                    deltas.Add(delta.Text);
                    break;
                case ChatCompletionResult terminal:
                    result = terminal;
                    break;
            }
        }
        result.Should().NotBeNull("the stream must end with a terminal completion record");
        return (deltas, result!);
    }

    // ----- streaming happy path -----

    [Fact]
    public async Task Deltas_concatenate_exactly_to_terminal_full_text_with_usage()
    {
        var (service, _) = ServiceOver(Stream(
            Start(inputTokens: 12),
            TextDeltaEvent("Hel"),
            TextDeltaEvent("lo "),
            TextDeltaEvent("world"),
            FinalDelta("end_turn", outputTokens: 34)));

        var (deltas, result) = await Drain(service, UserOnlyHistory);

        deltas.Should().Equal("Hel", "lo ", "world");
        string.Concat(deltas).Should().Be(result.FullText).And.Be("Hello world");
        result.StopReason.Should().Be(ChatCompletionStopReason.Complete);
        result.RefusalCategory.Should().BeNull();
        result.InputTokens.Should().Be(12);
        result.OutputTokens.Should().Be(34);
    }

    [Fact]
    public async Task Thinking_deltas_are_not_surfaced_as_text_deltas()
    {
        var (service, _) = ServiceOver(Stream(
            Start(inputTokens: 5),
            ThinkingDeltaEvent("pondering"),
            TextDeltaEvent("answer", index: 1),
            FinalDelta("end_turn", outputTokens: 7)));

        var (deltas, result) = await Drain(service, UserOnlyHistory);

        deltas.Should().Equal("answer");
        result.FullText.Should().Be("answer");
    }

    // ----- stop-reason taxonomy -----

    [Fact]
    public async Task Max_tokens_stop_reason_is_a_distinct_truncated_outcome()
    {
        var (service, _) = ServiceOver(Stream(
            Start(inputTokens: 3),
            TextDeltaEvent("partial"),
            FinalDelta("max_tokens", outputTokens: 64000)));

        var (_, result) = await Drain(service, UserOnlyHistory);

        result.StopReason.Should().Be(ChatCompletionStopReason.Truncated);
        result.FullText.Should().Be("partial");
    }

    [Fact]
    public async Task Refusal_is_a_typed_outcome_carrying_the_stop_details_category()
    {
        var (service, _) = ServiceOver(Stream(
            Start(inputTokens: 3),
            FinalDelta("refusal", outputTokens: 0, refusalCategory: "cyber")));

        var (deltas, result) = await Drain(service, UserOnlyHistory);

        deltas.Should().BeEmpty();
        result.StopReason.Should().Be(ChatCompletionStopReason.Refused);
        result.RefusalCategory.Should().Be("cyber");
        result.FullText.Should().BeEmpty("a refusal is surfaced as a typed outcome, never as a silently empty completion");
    }

    [Fact]
    public async Task Refusal_without_a_category_still_reports_refused()
    {
        var (service, _) = ServiceOver(Stream(
            Start(inputTokens: 3),
            FinalDelta("refusal", outputTokens: 0)));

        var (_, result) = await Drain(service, UserOnlyHistory);

        result.StopReason.Should().Be(ChatCompletionStopReason.Refused);
        result.RefusalCategory.Should().BeNull();
    }

    [Fact]
    public async Task Unknown_stop_reason_fails_fast()
    {
        var (service, _) = ServiceOver(Stream(
            Start(inputTokens: 3),
            FinalDelta("pause_turn", outputTokens: 1)));

        var act = () => Drain(service, UserOnlyHistory);

        var ex = (await act.Should().ThrowAsync<ChatCompletionException>()).Which;
        ex.Retryable.Should().BeFalse();
        ex.Message.Should().Contain("pause_turn");
    }

    [Fact]
    public async Task Stream_ending_without_a_stop_reason_fails_fast()
    {
        var (service, _) = ServiceOver(Stream(
            Start(inputTokens: 3),
            TextDeltaEvent("half a resp")));

        var act = () => Drain(service, UserOnlyHistory);

        (await act.Should().ThrowAsync<ChatCompletionException>())
            .Which.Retryable.Should().BeFalse();
    }

    // ----- exception taxonomy -----

    public static TheoryData<Exception, bool> SdkExceptionBuckets() => new()
    {
        { new AnthropicRateLimitException(new HttpRequestException("429")) { StatusCode = (HttpStatusCode)429, ResponseBody = "{}" }, true },
        { new Anthropic5xxException(new HttpRequestException("503")) { StatusCode = HttpStatusCode.ServiceUnavailable, ResponseBody = "{}" }, true },
        { new AnthropicIOException("connection reset", new HttpRequestException("io")), true },
        { new AnthropicBadRequestException(new HttpRequestException("400")) { StatusCode = HttpStatusCode.BadRequest, ResponseBody = "{}" }, false },
        { new AnthropicUnauthorizedException(new HttpRequestException("401")) { StatusCode = HttpStatusCode.Unauthorized, ResponseBody = "{}" }, false },
        { new AnthropicForbiddenException(new HttpRequestException("403")) { StatusCode = HttpStatusCode.Forbidden, ResponseBody = "{}" }, false },
        { new AnthropicNotFoundException(new HttpRequestException("404")) { StatusCode = HttpStatusCode.NotFound, ResponseBody = "{}" }, false },
    };

    [Theory]
    [MemberData(nameof(SdkExceptionBuckets))]
    public async Task Sdk_exceptions_map_to_the_retryable_taxonomy_with_the_original_as_inner(
        Exception sdkException, bool retryable)
    {
        var (service, _) = ServiceOver(Throwing(sdkException));

        var act = () => Drain(service, UserOnlyHistory);

        var ex = (await act.Should().ThrowAsync<ChatCompletionException>()).Which;
        ex.Retryable.Should().Be(retryable);
        ex.InnerException.Should().BeSameAs(sdkException);
    }

    [Fact]
    public async Task Cancellation_propagates_unwrapped()
    {
        var (service, _) = ServiceOver(Throwing(new OperationCanceledException()));

        var act = () => Drain(service, UserOnlyHistory);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ----- request shape -----

    [Fact]
    public async Task Request_uses_default_model_streaming_defaults_and_adaptive_thinking_without_sampling_params()
    {
        var (service, captured) = ServiceOver(Stream(
            Start(1), FinalDelta("end_turn", 1)));

        await Drain(service, UserOnlyHistory);

        var request = captured();
        request.Should().NotBeNull();
        request!.Model.Raw().Should().Be(Opus48);
        request.MaxTokens.Should().Be(ChatCompletionOptions.DefaultMaxTokens);
        request.Thinking!.TryPickAdaptive(out _).Should().BeTrue("adaptive thinking is always requested");
#pragma warning disable CS0618 // asserting the deprecated sampling params are never sent
        request.Temperature.Should().BeNull("temperature is removed on this model and would 400");
        request.TopP.Should().BeNull();
        request.TopK.Should().BeNull();
#pragma warning restore CS0618
    }

    [Fact]
    public async Task Request_options_override_model_and_max_tokens()
    {
        var (service, captured) = ServiceOver(Stream(
            Start(1), FinalDelta("end_turn", 1)));

        await Drain(service, UserOnlyHistory,
            new ChatCompletionRequestOptions { Model = "claude-sonnet-5", MaxTokens = 1234 });

        captured()!.Model.Raw().Should().Be("claude-sonnet-5");
        captured()!.MaxTokens.Should().Be(1234);
    }

    [Fact]
    public async Task System_role_messages_become_the_system_prompt_not_message_turns()
    {
        var (service, captured) = ServiceOver(Stream(
            Start(1), FinalDelta("end_turn", 1)));

        await Drain(service, new ChatCompletionMessage[]
        {
            new(ChatMessageRoles.System, "You are terse."),
            new(ChatMessageRoles.User, "Hi"),
            new(ChatMessageRoles.Assistant, "Hello."),
            new(ChatMessageRoles.User, "Ok"),
        });

        var request = captured()!;
        request.System!.TryPickString(out var system).Should().BeTrue();
        system.Should().Be("You are terse.");
        request.Messages.Select(m => m.Role.Raw()).Should().Equal("user", "assistant", "user");
        request.Messages.Select(m =>
        {
            m.Content.TryPickString(out var text).Should().BeTrue();
            return text;
        }).Should().Equal("Hi", "Hello.", "Ok");
    }

    [Fact]
    public async Task Multiple_system_messages_concatenate_into_one_system_prompt()
    {
        var (service, captured) = ServiceOver(Stream(
            Start(1), FinalDelta("end_turn", 1)));

        await Drain(service, new ChatCompletionMessage[]
        {
            new(ChatMessageRoles.System, "Rule one."),
            new(ChatMessageRoles.System, "Rule two."),
            new(ChatMessageRoles.User, "Hi"),
        });

        captured()!.System!.TryPickString(out var system).Should().BeTrue();
        system.Should().Be("Rule one.\n\nRule two.");
    }

    [Fact]
    public async Task No_system_messages_leaves_the_system_prompt_unset()
    {
        var (service, captured) = ServiceOver(Stream(
            Start(1), FinalDelta("end_turn", 1)));

        await Drain(service, UserOnlyHistory);

        captured()!.System.Should().BeNull();
    }

    // ----- input validation (fail fast, before any wire call) -----

    [Fact]
    public async Task Unknown_role_is_rejected()
    {
        var (service, _) = ServiceOver(Stream());

        var act = () => Drain(service, new ChatCompletionMessage[] { new("robot", "Hi") });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Empty_history_is_rejected()
    {
        var (service, _) = ServiceOver(Stream());

        var act = () => Drain(service, Array.Empty<ChatCompletionMessage>());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task History_of_only_system_messages_is_rejected()
    {
        var (service, _) = ServiceOver(Stream());

        var act = () => Drain(service, new ChatCompletionMessage[]
        {
            new(ChatMessageRoles.System, "You are terse."),
        });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Blank_message_content_is_rejected()
    {
        var (service, _) = ServiceOver(Stream());

        var act = () => Drain(service, new ChatCompletionMessage[]
        {
            new(ChatMessageRoles.User, "   "),
        });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ----- configuration / construction -----

    [Fact]
    public void Missing_api_key_fails_at_construction_with_a_clear_error()
    {
        var act = () => new AnthropicChatCompletionService(new ChatCompletionOptions { ApiKey = " " });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ApiKey*")
            .WithMessage("*ANTHROPIC_API_KEY*");
    }

    [Fact]
    public void Options_default_to_opus_and_a_streaming_sized_max_tokens()
    {
        var options = new ChatCompletionOptions { ApiKey = "sk-test" };

        options.Model.Should().Be(Opus48);
        options.MaxTokens.Should().Be(64000);
    }

    [Fact]
    public void FromConfiguration_reads_the_bifrost_chat_section()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BifrostQL:Chat:ApiKey"] = "sk-from-config",
            ["BifrostQL:Chat:Model"] = "claude-sonnet-5",
            ["BifrostQL:Chat:MaxTokens"] = "9000",
        }).Build();

        var options = ChatCompletionOptions.FromConfiguration(configuration);

        options.ApiKey.Should().Be("sk-from-config");
        options.Model.Should().Be("claude-sonnet-5");
        options.MaxTokens.Should().Be(9000);
    }

    [Fact]
    public void FromConfiguration_falls_back_to_the_anthropic_api_key_environment_variable()
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-from-env");
        try
        {
            var configuration = new ConfigurationBuilder().Build();

            var options = ChatCompletionOptions.FromConfiguration(configuration);

            options.ApiKey.Should().Be("sk-from-env");
            options.Model.Should().Be(Opus48);
            options.MaxTokens.Should().Be(64000);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previous);
        }
    }

    [Fact]
    public void FromConfiguration_prefers_config_over_the_environment_variable()
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-from-env");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BifrostQL:Chat:ApiKey"] = "sk-from-config",
            }).Build();

            ChatCompletionOptions.FromConfiguration(configuration).ApiKey.Should().Be("sk-from-config");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previous);
        }
    }
}
