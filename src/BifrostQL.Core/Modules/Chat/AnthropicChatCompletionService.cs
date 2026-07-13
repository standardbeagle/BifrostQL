using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
    /// taxonomy, exception taxonomy, the multi-turn tool-use loop) is this class and is
    /// unit-tested against faked SDK stream events.
    ///
    /// Tool loop (<see cref="ChatCompletionToolOptions"/>): on a <c>tool_use</c> stop
    /// the FULL assistant turn — thinking blocks included, the API requires them back
    /// verbatim when thinking is enabled — is appended to the conversation, every
    /// <c>tool_use</c> block of the turn is executed (parallel-safe), and ALL results
    /// return in ONE user message of <c>tool_result</c> blocks (the API contract), then
    /// the loop continues until a terminal stop or the iteration cap
    /// (<see cref="ChatToolLoopLimitException"/>). A connector throw becomes an
    /// <c>is_error</c> tool result fed back to the model — SANITIZED to the tool name
    /// plus exception type (full detail logged server-side; only a
    /// <see cref="ChatToolInputException"/> message travels verbatim), because the
    /// tool_result content goes off-box to the provider. The stream itself never
    /// crashes on a tool failure. No retries and no fallbacks live here — provider
    /// failures surface as <see cref="ChatCompletionException"/> with a
    /// <c>Retryable</c> flag and the caller decides.
    /// </summary>
    public sealed class AnthropicChatCompletionService : IChatCompletionService, IDisposable
    {
        // Wire stop reasons this service can receive for the request shapes it sends
        // (no stop sequences; tools only when configured). Anything else is a
        // contract violation.
        private const string StopEndTurn = "end_turn";
        private const string StopMaxTokens = "max_tokens";
        private const string StopRefusal = "refusal";
        private const string StopToolUse = "tool_use";

        // Tool-activity summaries are display material for transports, not payloads.
        private const int SummaryLimit = 160;

        private readonly IMessageService _messages;
        private readonly ChatCompletionOptions _options;
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private readonly AnthropicClient? _ownedClient;

        /// <summary>
        /// Production constructor. Fails fast on a missing api key so a misconfigured
        /// host errors at wiring time, not on the first chat request. The logger
        /// receives the FULL detail of sanitized tool failures — the raw exception
        /// stays server-side while the model sees only the safe summary.
        /// </summary>
        public AnthropicChatCompletionService(
            ChatCompletionOptions options,
            Microsoft.Extensions.Logging.ILogger<AnthropicChatCompletionService>? logger = null)
            : this(CreateClient(options), options, logger)
        {
        }

        /// <summary>
        /// Test seam: injects the SDK's message service directly (the system boundary the
        /// tests fake). Production always enters through the options constructor.
        /// </summary>
        internal AnthropicChatCompletionService(
            IMessageService messages,
            ChatCompletionOptions options,
            Microsoft.Extensions.Logging.ILogger? logger = null)
        {
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _options = ValidateOptions(options);
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        private AnthropicChatCompletionService(
            AnthropicClient client,
            ChatCompletionOptions options,
            Microsoft.Extensions.Logging.ILogger? logger)
            : this(client.Messages, options, logger)
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
            var (shape, turns) = BuildRequest(history, options);
            return StreamCore(shape, turns, options?.Tools, cancellationToken);
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

        /// <summary>The per-completion request fields that never change across loop turns.</summary>
        private sealed record RequestShape(
            string Model,
            int MaxTokens,
            MessageCreateParamsSystem? System,
            IReadOnlyList<ToolUnion>? Tools);

        private (RequestShape Shape, List<MessageParam> Turns) BuildRequest(
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

            var shape = new RequestShape(
                options?.Model ?? _options.Model,
                options?.MaxTokens ?? _options.MaxTokens,
                systemParts.Count > 0
                    ? (MessageCreateParamsSystem)string.Join("\n\n", systemParts)
                    : null,
                MapTools(options?.Tools));

            return (shape, turns);
        }

        // Maps the connector tool definitions onto the wire tools param, fail-fast on
        // shapes the API would reject: empty tool sets, blank descriptions, and input
        // schemas that are not JSON objects are caller/connector bugs, not 400s to
        // discover on the first chat request.
        private static IReadOnlyList<ToolUnion>? MapTools(ChatCompletionToolOptions? tools)
        {
            if (tools is null)
                return null;
            if (tools.Tools.Count == 0)
                throw new ArgumentException(
                    "Tool options were supplied with no tools; omit the options instead.", nameof(tools));
            if (tools.MaxToolIterations < 1)
                throw new ArgumentException(
                    "MaxToolIterations must be at least 1.", nameof(tools));

            return tools.Tools.Select(MapToolDefinition).ToList();
        }

        private static ToolUnion MapToolDefinition(ChatToolDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(definition.Description))
                throw new InvalidOperationException(
                    $"Chat tool '{definition.Name}' has no description; the model cannot know when to call it.");

            InputSchema? schema;
            try
            {
                schema = JsonSerializer.Deserialize<InputSchema>(definition.InputSchemaJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Chat tool '{definition.Name}' has an invalid input schema; it must be a JSON Schema object.", ex);
            }

            if (schema is null)
                throw new InvalidOperationException(
                    $"Chat tool '{definition.Name}' has an empty input schema; it must be a JSON Schema object.");

            return new Tool
            {
                Name = definition.Name,
                Description = definition.Description,
                InputSchema = schema,
            };
        }

        private MessageCreateParams BuildTurnParams(RequestShape shape, List<MessageParam> turns) => new()
        {
            Model = shape.Model,
            MaxTokens = shape.MaxTokens,
            // Snapshot: the loop appends to `turns`; each wire request owns its list.
            Messages = turns.ToList(),
            // Adaptive thinking, always. No temperature/top_p/top_k and no
            // budget_tokens — all removed on the default model (400 if sent).
            Thinking = new ThinkingConfigAdaptive(),
            System = shape.System,
            Tools = shape.Tools,
        };

        private async IAsyncEnumerable<ChatCompletionEvent> StreamCore(
            RequestShape shape,
            List<MessageParam> turns,
            ChatCompletionToolOptions? tools,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var text = new StringBuilder();
            long inputTokens = 0;
            long outputTokens = 0;
            // Tools whose results park on a user confirmation (plan tools): their
            // presence in a turn switches that turn's tool execution to serial.
            var confirmationTools = new HashSet<string>(
                (tools?.Tools ?? Array.Empty<ChatToolDefinition>())
                    .Where(t => t.RequiresConfirmation)
                    .Select(t => t.Name),
                StringComparer.Ordinal);

            for (var turnNumber = 1; ; turnNumber++)
            {
                // The cap counts model turns; turn N ending in tool_use needs turn N+1,
                // so exceeding the cap surfaces BEFORE another provider call is made.
                if (tools is not null && turnNumber > tools.MaxToolIterations)
                    throw new ChatToolLoopLimitException(tools.MaxToolIterations);

                var turn = new TurnAccumulator();
                string? stopReason = null;
                string? refusalCategory = null;
                long turnInputTokens = 0;
                long turnOutputTokens = 0;

                IAsyncEnumerator<RawMessageStreamEvent> enumerator;
                try
                {
                    enumerator = _messages.CreateStreaming(BuildTurnParams(shape, turns), cancellationToken)
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
                            // Assistant text is surfaced as deltas; thinking/signature/
                            // tool-input deltas are accumulated for the turn replay but
                            // are not part of the completion text.
                            if (blockDelta.Delta.TryPickText(out var textDelta))
                            {
                                turn.AppendText(blockDelta.Index, textDelta.Text);
                                text.Append(textDelta.Text);
                                yield return new ChatCompletionDelta(textDelta.Text);
                            }
                            else if (blockDelta.Delta.TryPickInputJson(out var inputJson))
                            {
                                turn.AppendToolInputJson(blockDelta.Index, inputJson.PartialJson);
                            }
                            else if (blockDelta.Delta.TryPickThinking(out var thinkingDelta))
                            {
                                turn.AppendThinking(blockDelta.Index, thinkingDelta.Thinking);
                            }
                            else if (blockDelta.Delta.TryPickSignature(out var signatureDelta))
                            {
                                turn.AppendSignature(blockDelta.Index, signatureDelta.Signature);
                            }
                        }
                        else if (streamEvent.TryPickContentBlockStart(out var blockStart))
                        {
                            turn.StartBlock(blockStart.Index, blockStart.ContentBlock);
                        }
                        else if (streamEvent.TryPickStart(out var start))
                        {
                            turnInputTokens = start.Message?.Usage?.InputTokens ?? 0;
                        }
                        else if (streamEvent.TryPickDelta(out var messageDelta))
                        {
                            stopReason = messageDelta.Delta?.StopReason?.Raw();
                            refusalCategory = messageDelta.Delta?.StopDetails?.Category?.Raw();
                            if (messageDelta.Usage is { } usage)
                            {
                                turnOutputTokens = usage.OutputTokens;
                                if (usage.InputTokens is { } deltaInputTokens)
                                    turnInputTokens = deltaInputTokens;
                            }
                        }
                    }
                }

                inputTokens += turnInputTokens;
                outputTokens += turnOutputTokens;

                if (stopReason == StopToolUse && tools is not null)
                {
                    var toolUses = turn.ToolUses();
                    if (toolUses.Count == 0)
                        throw new ChatCompletionException(
                            "The completion stopped for tool use but streamed no tool_use block.", retryable: false);

                    // The assistant turn goes back VERBATIM — thinking blocks included,
                    // the API rejects a tool continuation that drops them.
                    turns.Add(new MessageParam { Role = Role.Assistant, Content = turn.BuildAssistantBlocks() });

                    foreach (var toolUse in toolUses)
                        yield return new ChatToolActivity(toolUse.Name, ChatToolPhase.Call, Summarize(toolUse.InputJson));

                    // ALL tool calls of the turn execute and ALL results return in ONE
                    // user message — the Anthropic tool_result contract. Parallel-safe,
                    // EXCEPT when the turn requests a confirmation-gated (plan) tool:
                    // then the calls run SERIALLY so at most one write proposal parks
                    // awaiting the user at a time, in tool-block order.
                    var serial = toolUses.Any(toolUse => confirmationTools.Contains(toolUse.Name));
                    var parallelResults = serial
                        ? null
                        : await Task.WhenAll(
                                toolUses.Select(toolUse => ExecuteToolAsync(tools.Executor, toolUse, cancellationToken)))
                            .ConfigureAwait(false);

                    var resultBlocks = new List<ContentBlockParam>(toolUses.Count);
                    for (var i = 0; i < toolUses.Count; i++)
                    {
                        var result = parallelResults is not null
                            ? parallelResults[i]
                            : await ExecuteToolAsync(tools.Executor, toolUses[i], cancellationToken)
                                .ConfigureAwait(false);

                        if (result.ConfirmationRequest is { } confirmation)
                        {
                            // A write proposal: relay it, then PARK until the user's
                            // decision (or the timeout's deny) resolves it. The park
                            // happens here, BETWEEN provider turns — this turn's stream
                            // was fully drained above (stop_reason tool_use consumed),
                            // so no Anthropic HTTP request is held open while waiting.
                            yield return new ChatToolConfirmationActivity(toolUses[i].Name, confirmation);
                            var outcome = await ResolveConfirmationAsync(confirmation, toolUses[i].Name, cancellationToken)
                                .ConfigureAwait(false);
                            yield return new ChatToolConfirmationDecisionActivity(
                                toolUses[i].Name,
                                confirmation.ConfirmationId,
                                confirmation.Table,
                                confirmation.Operation,
                                outcome.Approved,
                                outcome.Reason);
                            result = outcome.Result;
                        }

                        yield return new ChatToolActivity(
                            toolUses[i].Name,
                            ChatToolPhase.Result,
                            result.IsError ? $"error: {Summarize(result.TextPayload)}" : Summarize(result.TextPayload));
                        // Media references ride the STREAM (transports relay them to
                        // clients), never the model conversation.
                        if (result.MediaReferences is { Count: > 0 } mediaReferences)
                            yield return new ChatToolMediaActivity(toolUses[i].Name, mediaReferences);
                        resultBlocks.Add(new ToolResultBlockParam
                        {
                            ToolUseID = toolUses[i].Id,
                            Content = BuildToolResultContent(result),
                            IsError = result.IsError ? true : null,
                        });
                    }
                    turns.Add(new MessageParam { Role = Role.User, Content = resultBlocks });
                    continue;
                }

                yield return new ChatCompletionResult(
                    text.ToString(),
                    MapStopReason(stopReason),
                    refusalCategory,
                    inputTokens,
                    outputTokens);
                yield break;
            }
        }

        // A connector throw is a TOOL failure, not a stream failure: it feeds back to
        // the model as an is_error result and the loop continues.
        //
        // Two deliberate boundaries here:
        //  - CANCELLATION ORIGIN: only the REQUEST token's cancellation is caller
        //    teardown and rethrows. An OperationCanceledException from a connector's
        //    own internal token (HTTP client timeout, self-imposed deadline) while the
        //    request is still alive is a tool fault like any other.
        //  - SANITIZATION: the tool_result content is sent OFF-BOX to the Anthropic
        //    API, and raw exception messages routinely carry connection strings and
        //    other server-side detail. The model therefore sees only the tool name and
        //    the exception TYPE; the full exception is logged server-side. The one
        //    sanctioned model-visible channel is ChatToolInputException, whose message
        //    the connector authored FOR the model.
        private async Task<ChatToolResult> ExecuteToolAsync(
            IChatToolExecutor executor, ToolUseRequest toolUse, CancellationToken cancellationToken)
        {
            try
            {
                var result = await executor.ExecuteAsync(toolUse.Name, toolUse.InputJson, cancellationToken)
                    .ConfigureAwait(false);
                return result ?? new ChatToolResult
                {
                    TextPayload = $"The tool '{toolUse.Name}' returned no result.",
                    IsError = true,
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ChatToolInputException ex)
            {
                return new ChatToolResult { TextPayload = ex.Message, IsError = true };
            }
            catch (Exception ex)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogError(_logger, ex,
                    "Chat tool {ToolName} failed; a sanitized error was fed back to the model.", toolUse.Name);
                return new ChatToolResult
                {
                    TextPayload = $"Tool '{toolUse.Name}' failed: {ex.GetType().Name}.",
                    IsError = true,
                };
            }
        }

        // Parks on a confirmation request's resolution with the same two boundaries as
        // ExecuteToolAsync: only the REQUEST token's cancellation is caller teardown
        // (rethrows — the proposal's registry entry died with the request); any other
        // failure is SANITIZED before it reaches the provider, reported as a denied
        // outcome so the transcript never claims an approval that produced no result.
        private async Task<ChatToolConfirmationOutcome> ResolveConfirmationAsync(
            ChatToolConfirmationRequest confirmation, string toolName, CancellationToken cancellationToken)
        {
            try
            {
                return await confirmation.ResolveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ChatToolInputException ex)
            {
                return new ChatToolConfirmationOutcome(false, ex.Message,
                    new ChatToolResult { TextPayload = ex.Message, IsError = true });
            }
            catch (Exception ex)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogError(_logger, ex,
                    "Chat tool {ToolName} confirmation resolution failed; a sanitized error was fed back to the model.",
                    toolName);
                return new ChatToolConfirmationOutcome(false, null, new ChatToolResult
                {
                    TextPayload = $"Tool '{toolName}' failed: {ex.GetType().Name}.",
                    IsError = true,
                });
            }
        }

        // A vision-bearing result becomes a tool_result content BLOCK LIST — the text
        // payload plus one base64 image block — instead of the plain string; that is
        // the wire shape the Anthropic API defines for images inside tool results.
        // Everything else stays the plain string it always was.
        private static ToolResultBlockParamContent BuildToolResultContent(ChatToolResult result)
        {
            if (result.VisionImage is not { } image)
                return result.TextPayload;

            return new List<Block>
            {
                new TextBlockParam(result.TextPayload),
                new ImageBlockParam(new Base64ImageSource
                {
                    Data = Convert.ToBase64String(image.Data),
                    MediaType = image.MediaType,
                }),
            };
        }

        private static string Summarize(string payload)
        {
            var trimmed = payload.Trim();
            return trimmed.Length <= SummaryLimit ? trimmed : trimmed[..(SummaryLimit - 1)] + "…";
        }

        /// <summary>One tool call the model requested in a turn.</summary>
        private sealed record ToolUseRequest(string Id, string Name, string InputJson);

        /// <summary>
        /// Accumulates one model turn's content blocks by stream index so a tool_use
        /// turn can be replayed verbatim as the next request's assistant message:
        /// text, thinking (with signature), redacted thinking, and tool_use input
        /// JSON assembled from <c>input_json_delta</c> fragments. Block kinds outside
        /// those four belong to server tools this service never requests and are
        /// ignored like every other model-internal shape.
        /// </summary>
        private sealed class TurnAccumulator
        {
            private sealed class Block
            {
                public required string Kind { get; init; }
                public StringBuilder Text { get; } = new();
                public StringBuilder Signature { get; } = new();
                public string? ToolId { get; init; }
                public string? ToolName { get; init; }
                public string? RedactedData { get; init; }
            }

            private const string KindText = "text";
            private const string KindThinking = "thinking";
            private const string KindRedacted = "redacted_thinking";
            private const string KindToolUse = "tool_use";

            private readonly SortedDictionary<long, Block> _blocks = new();

            public void StartBlock(long index, RawContentBlockStartEventContentBlock content)
            {
                if (content.TryPickToolUse(out var toolUse))
                    _blocks[index] = new Block { Kind = KindToolUse, ToolId = toolUse.ID, ToolName = toolUse.Name };
                else if (content.TryPickText(out var textBlock))
                    GetOrAdd(index, KindText).Text.Append(textBlock.Text);
                else if (content.TryPickThinking(out var thinkingBlock))
                    GetOrAdd(index, KindThinking).Text.Append(thinkingBlock.Thinking);
                else if (content.TryPickRedactedThinking(out var redacted))
                    _blocks[index] = new Block { Kind = KindRedacted, RedactedData = redacted.Data };
            }

            public void AppendText(long index, string text) => GetOrAdd(index, KindText).Text.Append(text);

            public void AppendThinking(long index, string thinking) => GetOrAdd(index, KindThinking).Text.Append(thinking);

            public void AppendSignature(long index, string signature) =>
                GetOrAdd(index, KindThinking).Signature.Append(signature);

            public void AppendToolInputJson(long index, string partialJson)
            {
                if (!_blocks.TryGetValue(index, out var block) || block.Kind != KindToolUse)
                    throw new ChatCompletionException(
                        "The completion streamed tool-input JSON for a block that is not an open tool_use block.",
                        retryable: false);
                block.Text.Append(partialJson);
            }

            public IReadOnlyList<ToolUseRequest> ToolUses() =>
                _blocks.Values
                    .Where(b => b.Kind == KindToolUse)
                    .Select(b => new ToolUseRequest(b.ToolId!, b.ToolName!, NormalizeInputJson(b)))
                    .ToList();

            public List<ContentBlockParam> BuildAssistantBlocks()
            {
                var blocks = new List<ContentBlockParam>(_blocks.Count);
                foreach (var block in _blocks.Values)
                {
                    switch (block.Kind)
                    {
                        case KindText when block.Text.Length > 0:
                            blocks.Add(new TextBlockParam(block.Text.ToString()));
                            break;
                        case KindThinking:
                            blocks.Add(new ThinkingBlockParam
                            {
                                Thinking = block.Text.ToString(),
                                Signature = block.Signature.ToString(),
                            });
                            break;
                        case KindRedacted:
                            blocks.Add(new RedactedThinkingBlockParam { Data = block.RedactedData! });
                            break;
                        case KindToolUse:
                            blocks.Add(new ToolUseBlockParam
                            {
                                ID = block.ToolId!,
                                Name = block.ToolName!,
                                Input = ParseToolInput(NormalizeInputJson(block), block.ToolName!),
                            });
                            break;
                    }
                }
                return blocks;
            }

            // A tool call with no arguments streams no input_json_delta at all; the
            // executor contract promises a JSON object, so blank means {}.
            private static string NormalizeInputJson(Block block)
            {
                var json = block.Text.ToString();
                return string.IsNullOrWhiteSpace(json) ? "{}" : json;
            }

            private static IReadOnlyDictionary<string, JsonElement> ParseToolInput(string json, string toolName)
            {
                try
                {
                    return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                        ?? throw new ChatCompletionException(
                            $"The completion streamed a null argument object for tool '{toolName}'.", retryable: false);
                }
                catch (JsonException ex)
                {
                    throw new ChatCompletionException(
                        $"The completion streamed malformed argument JSON for tool '{toolName}'.", retryable: false, ex);
                }
            }

            private Block GetOrAdd(long index, string kind)
            {
                if (_blocks.TryGetValue(index, out var block))
                    return block;
                block = new Block { Kind = kind };
                _blocks[index] = block;
                return block;
            }
        }

        // The closed TERMINAL stop-reason taxonomy. tool_use is consumed by the loop
        // (never terminal); reaching here with it — or with pause_turn, stop_sequence,
        // or a future value — is a contract violation: fail fast rather than mislabel
        // the outcome.
        private static ChatCompletionStopReason MapStopReason(string? stopReason) => stopReason switch
        {
            StopEndTurn => ChatCompletionStopReason.Complete,
            StopMaxTokens => ChatCompletionStopReason.Truncated,
            StopRefusal => ChatCompletionStopReason.Refused,
            StopToolUse => throw new ChatCompletionException(
                "The completion stopped for tool use but the request carried no tools.", retryable: false),
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
