using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Services;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// <see cref="IChatCompletionService"/> over the official Anthropic C# SDK. Always
    /// streams (<see cref="IMessageService.CreateStreaming"/>) with adaptive thinking and
    /// no sampling parameters — <c>temperature</c>/<c>top_p</c>/<c>top_k</c> are removed
    /// on the default model and would 400. The SDK's <see cref="IMessageService"/> is the
    /// system boundary: everything above it (request shape, delta mapping, stop-reason
    /// taxonomy, exception taxonomy) is this class and is unit-tested against faked SDK
    /// stream events. No retries and no fallbacks live here — failures surface as
    /// <see cref="ChatCompletionException"/> with a <c>Retryable</c> flag and the caller
    /// decides.
    /// </summary>
    public sealed class AnthropicChatCompletionService : IChatCompletionService, IDisposable
    {
        // Wire stop reasons this service can receive for the request shape it sends
        // (no tools, no stop sequences). Anything else is a contract violation.
        private const string StopEndTurn = "end_turn";
        private const string StopMaxTokens = "max_tokens";
        private const string StopRefusal = "refusal";

        private readonly IMessageService _messages;
        private readonly ChatCompletionOptions _options;
        private readonly AnthropicClient? _ownedClient;

        /// <summary>
        /// Production constructor. Fails fast on a missing api key so a misconfigured
        /// host errors at wiring time, not on the first chat request.
        /// </summary>
        public AnthropicChatCompletionService(ChatCompletionOptions options)
            : this(CreateClient(options), options)
        {
        }

        /// <summary>
        /// Test seam: injects the SDK's message service directly (the system boundary the
        /// tests fake). Production always enters through the options constructor.
        /// </summary>
        internal AnthropicChatCompletionService(IMessageService messages, ChatCompletionOptions options)
        {
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _options = ValidateOptions(options);
        }

        private AnthropicChatCompletionService(AnthropicClient client, ChatCompletionOptions options)
            : this(client.Messages, options)
        {
            _ownedClient = client;
        }

        public void Dispose() => _ownedClient?.Dispose();

        /// <inheritdoc />
        public IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            IReadOnlyList<ChatCompletionMessage> history,
            ChatCompletionRequestOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // Validate + build the request eagerly so caller bugs throw at the call
            // site instead of on first enumeration of the deferred iterator.
            var parameters = BuildRequest(history, options);
            return StreamCore(parameters, cancellationToken);
        }

        private static AnthropicClient CreateClient(ChatCompletionOptions options)
        {
            ValidateOptions(options);
            if (string.IsNullOrWhiteSpace(options.ApiKey))
                throw new InvalidOperationException(
                    $"The Anthropic chat completion service has no ApiKey. Set '{ChatCompletionOptions.SectionName}:ApiKey' " +
                    $"in configuration or the {ChatCompletionOptions.ApiKeyEnvironmentVariable} environment variable.");

            return new AnthropicClient { ApiKey = options.ApiKey };
        }

        private static ChatCompletionOptions ValidateOptions(ChatCompletionOptions options)
        {
            if (options is null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.Model))
                throw new InvalidOperationException("ChatCompletionOptions.Model must not be empty.");
            if (options.MaxTokens <= 0)
                throw new InvalidOperationException("ChatCompletionOptions.MaxTokens must be positive.");
            return options;
        }

        private MessageCreateParams BuildRequest(
            IReadOnlyList<ChatCompletionMessage> history,
            ChatCompletionRequestOptions? options)
        {
            if (history is null)
                throw new ArgumentNullException(nameof(history));
            if (history.Count == 0)
                throw new ArgumentException("The message history must not be empty.", nameof(history));

            var systemParts = new List<string>();
            var turns = new List<MessageParam>();
            foreach (var message in history)
            {
                var role = ChatMessageRoles.Normalize(message.Role);
                if (string.IsNullOrWhiteSpace(message.Content))
                    throw new ArgumentException(
                        $"A '{role}' message has blank content; every message must carry text.", nameof(history));

                // System turns are not message turns on the wire — the API accepts only
                // user/assistant in messages; system text rides the system prompt param.
                if (role == ChatMessageRoles.System)
                    systemParts.Add(message.Content);
                else
                    turns.Add(new MessageParam
                    {
                        Role = role == ChatMessageRoles.User ? Role.User : Role.Assistant,
                        Content = message.Content,
                    });
            }

            if (turns.Count == 0)
                throw new ArgumentException(
                    "The message history contains only system messages; at least one user or assistant turn is required.",
                    nameof(history));

            return new MessageCreateParams
            {
                Model = options?.Model ?? _options.Model,
                MaxTokens = options?.MaxTokens ?? _options.MaxTokens,
                Messages = turns,
                // Adaptive thinking, always. No temperature/top_p/top_k and no
                // budget_tokens — all removed on the default model (400 if sent).
                Thinking = new ThinkingConfigAdaptive(),
                System = systemParts.Count > 0
                    ? (MessageCreateParamsSystem)string.Join("\n\n", systemParts)
                    : null,
            };
        }

        private async IAsyncEnumerable<ChatCompletionEvent> StreamCore(
            MessageCreateParams parameters,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var text = new StringBuilder();
            long inputTokens = 0;
            long outputTokens = 0;
            string? stopReason = null;
            string? refusalCategory = null;

            IAsyncEnumerator<RawMessageStreamEvent> enumerator;
            try
            {
                enumerator = _messages.CreateStreaming(parameters, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception ex) when (TryMapSdkException(ex, out var mapped))
            {
                throw mapped;
            }

            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (TryMapSdkException(ex, out var mapped))
                    {
                        throw mapped;
                    }

                    if (!moved)
                        break;

                    var streamEvent = enumerator.Current;
                    if (streamEvent.TryPickContentBlockDelta(out var blockDelta))
                    {
                        // Only assistant text is surfaced; thinking/signature/citation
                        // deltas are model-internal and not part of the completion text.
                        if (blockDelta.Delta.TryPickText(out var textDelta))
                        {
                            text.Append(textDelta.Text);
                            yield return new ChatCompletionDelta(textDelta.Text);
                        }
                    }
                    else if (streamEvent.TryPickStart(out var start))
                    {
                        inputTokens = start.Message?.Usage?.InputTokens ?? 0;
                    }
                    else if (streamEvent.TryPickDelta(out var messageDelta))
                    {
                        stopReason = messageDelta.Delta?.StopReason?.Raw();
                        refusalCategory = messageDelta.Delta?.StopDetails?.Category?.Raw();
                        if (messageDelta.Usage is { } usage)
                        {
                            outputTokens = usage.OutputTokens;
                            if (usage.InputTokens is { } deltaInputTokens)
                                inputTokens = deltaInputTokens;
                        }
                    }
                }
            }

            yield return new ChatCompletionResult(
                text.ToString(),
                MapStopReason(stopReason),
                refusalCategory,
                inputTokens,
                outputTokens);
        }

        // The closed stop-reason taxonomy. A reason outside it (tool_use, pause_turn,
        // stop_sequence — shapes this service never requests — or a future value) is a
        // contract violation: fail fast rather than mislabel the outcome.
        private static ChatCompletionStopReason MapStopReason(string? stopReason) => stopReason switch
        {
            StopEndTurn => ChatCompletionStopReason.Complete,
            StopMaxTokens => ChatCompletionStopReason.Truncated,
            StopRefusal => ChatCompletionStopReason.Refused,
            null => throw new ChatCompletionException(
                "The completion stream ended without reporting a stop reason.", retryable: false),
            _ => throw new ChatCompletionException(
                $"Unknown completion stop reason '{stopReason}'.", retryable: false),
        };

        // Retryable: rate limits, 5xx, and connection/IO failures. Non-retryable: every
        // other provider error (4xx incl. auth, SSE protocol violations). Cancellation
        // and non-SDK exceptions propagate unmapped.
        private static bool TryMapSdkException(Exception exception, out ChatCompletionException mapped)
        {
            mapped = exception switch
            {
                OperationCanceledException => null!,
                AnthropicRateLimitException e => new ChatCompletionException(
                    "The Anthropic API rate-limited the completion request.", retryable: true, e),
                Anthropic5xxException e => new ChatCompletionException(
                    "The Anthropic API failed with a server error.", retryable: true, e),
                AnthropicIOException e => new ChatCompletionException(
                    "The connection to the Anthropic API failed.", retryable: true, e),
                Anthropic4xxException e => new ChatCompletionException(
                    "The Anthropic API rejected the completion request.", retryable: false, e),
                AnthropicException e => new ChatCompletionException(
                    "The Anthropic completion request failed.", retryable: false, e),
                _ => null!,
            };
            return mapped is not null;
        }
    }
}
