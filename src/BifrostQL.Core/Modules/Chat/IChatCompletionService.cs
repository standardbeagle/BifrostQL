using System;
using System.Collections.Generic;
using System.Threading;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// The chat module's single narrow seam onto the LLM. Implementations stream a
    /// completion for an ordered message history: zero or more
    /// <see cref="ChatCompletionDelta"/> text deltas — interleaved with
    /// <see cref="ChatToolActivity"/> events when the request carries tools — followed
    /// by exactly one terminal <see cref="ChatCompletionResult"/>. The multi-turn
    /// tool-use loop lives INSIDE this seam: tool calls, tool results, and the
    /// continuation turns are provider wire shapes, so nothing else in Core touches
    /// them — callers supply <see cref="ChatCompletionToolOptions"/> and consume this
    /// contract only.
    /// </summary>
    public interface IChatCompletionService
    {
        /// <summary>
        /// Streams a completion for <paramref name="history"/> (oldest first, roles from
        /// <see cref="ChatMessageRoles"/>; system messages become the system prompt).
        /// Provider failures surface as <see cref="ChatCompletionException"/> — retryable
        /// or not per its <see cref="ChatCompletionException.Retryable"/> flag; this
        /// service never retries or falls back itself, the caller decides.
        /// </summary>
        IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            IReadOnlyList<ChatCompletionMessage> history,
            ChatCompletionRequestOptions? options = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>One turn of the conversation history sent to the model.</summary>
    /// <param name="Role">A <see cref="ChatMessageRoles"/> value; anything else is rejected.</param>
    /// <param name="Content">The turn's text; blank content is rejected fail-fast.</param>
    public sealed record ChatCompletionMessage(string Role, string Content);

    /// <summary>Per-request overrides; unset values fall back to <see cref="ChatCompletionOptions"/>.</summary>
    public sealed record ChatCompletionRequestOptions
    {
        /// <summary>Model id override (exact string, no date suffix).</summary>
        public string? Model { get; init; }

        /// <summary>Max output tokens override.</summary>
        public int? MaxTokens { get; init; }

        /// <summary>
        /// The tools offered to the model plus the executor that runs them. Null (the
        /// default) sends no tools and the completion is a single model turn.
        /// </summary>
        public ChatCompletionToolOptions? Tools { get; init; }
    }

    /// <summary>
    /// Tool configuration for one completion. The executor is already bound to the
    /// caller's auth context (see <c>ChatToolSet.CreateExecutor</c>) — identity travels
    /// with the request, never ambiently.
    /// </summary>
    public sealed record ChatCompletionToolOptions
    {
        /// <summary>The default <see cref="MaxToolIterations"/> cap.</summary>
        public const int DefaultMaxToolIterations = 8;

        /// <summary>The tool definitions sent to the model. Must be non-empty.</summary>
        public required IReadOnlyList<ChatToolDefinition> Tools { get; init; }

        /// <summary>Executes the model's tool calls under the caller's identity.</summary>
        public required IChatToolExecutor Executor { get; init; }

        /// <summary>
        /// Maximum number of model turns (tool round-trips + the final answer) per
        /// completion. Exceeding it raises <see cref="ChatToolLoopLimitException"/> —
        /// a typed error, so no partial assistant text is ever persisted as an answer.
        /// </summary>
        public int MaxToolIterations { get; init; } = DefaultMaxToolIterations;
    }

    /// <summary>Base of the stream event shapes; consumers switch on the concrete type.</summary>
    public abstract record ChatCompletionEvent;

    /// <summary>One incremental chunk of assistant text.</summary>
    public sealed record ChatCompletionDelta(string Text) : ChatCompletionEvent;

    /// <summary>
    /// Tool-loop progress: emitted once per tool call when the model requests it
    /// (<see cref="ChatToolPhase.Call"/>) and once when its result is fed back
    /// (<see cref="ChatToolPhase.Result"/>), in tool-block order, so transports can
    /// relay live tool activity between text deltas. <see cref="Summary"/> is a short,
    /// truncated rendering of the input/result — display material, not the payload.
    /// </summary>
    public sealed record ChatToolActivity(string ToolName, ChatToolPhase Phase, string Summary) : ChatCompletionEvent;

    /// <summary>
    /// Media rows a tool result referenced (<see cref="ChatToolResult.MediaReferences"/>),
    /// emitted once per media-bearing tool result immediately after its
    /// <see cref="ChatToolPhase.Result"/> activity so transports can relay the
    /// references to clients (the chat middleware maps this to an SSE <c>media</c>
    /// event). Display/fetch material for the CLIENT — never part of the model
    /// conversation.
    /// </summary>
    public sealed record ChatToolMediaActivity(
        string ToolName, IReadOnlyList<ChatToolMediaReference> Items) : ChatCompletionEvent;

    /// <summary>
    /// A plan tool parked a write proposal awaiting the user's confirmation, emitted
    /// after the tool's <see cref="ChatToolPhase.Call"/> activity and BEFORE the loop
    /// parks on the decision — transports relay it (the chat middleware maps it to an
    /// SSE <c>confirmation</c> event) so the client can prompt the user. The loop
    /// yields no further events for this tool until the proposal resolves.
    /// </summary>
    public sealed record ChatToolConfirmationActivity(
        string ToolName, ChatToolConfirmationRequest Request) : ChatCompletionEvent;

    /// <summary>
    /// A parked proposal resolved (user confirm/deny, or the timeout's deny), emitted
    /// before the tool's <see cref="ChatToolPhase.Result"/> activity so transports can
    /// record the outcome (the chat middleware appends a system-role transcript row and
    /// relays an SSE <c>confirmation-resolved</c> event). <see cref="Approved"/> is the
    /// user's decision — an approved-but-vetoed write still reports <c>true</c> here and
    /// carries the veto as an <c>is_error</c> tool result.
    /// </summary>
    public sealed record ChatToolConfirmationDecisionActivity(
        string ToolName,
        string ConfirmationId,
        string Table,
        string Operation,
        bool Approved,
        string? Reason) : ChatCompletionEvent;

    /// <summary>Which side of a tool round-trip a <see cref="ChatToolActivity"/> reports.</summary>
    public enum ChatToolPhase
    {
        /// <summary>The model requested the tool call (before execution).</summary>
        Call,

        /// <summary>The tool executed and its result is being fed back to the model.</summary>
        Result,
    }

    /// <summary>
    /// The terminal completion record — always the last event of a successful stream.
    /// <see cref="FullText"/> is exactly the concatenation of the preceding deltas.
    /// </summary>
    /// <param name="FullText">
    /// The complete assistant text (empty on a pre-output refusal). With tools this
    /// spans every model turn of the loop — still exactly the concatenation of the
    /// preceding text deltas.
    /// </param>
    /// <param name="StopReason">The typed outcome; see <see cref="ChatCompletionStopReason"/>.</param>
    /// <param name="RefusalCategory">
    /// The provider's refusal category (e.g. "cyber") when <see cref="StopReason"/> is
    /// <see cref="ChatCompletionStopReason.Refused"/> and the provider reported one; null otherwise.
    /// </param>
    /// <param name="InputTokens">Prompt tokens billed, summed over every model turn of the loop.</param>
    /// <param name="OutputTokens">Completion tokens billed, summed over every model turn of the loop.</param>
    public sealed record ChatCompletionResult(
        string FullText,
        ChatCompletionStopReason StopReason,
        string? RefusalCategory,
        long InputTokens,
        long OutputTokens) : ChatCompletionEvent;

    /// <summary>
    /// The closed outcome taxonomy of the TERMINAL result. A tool-use stop is consumed
    /// inside the loop (it continues the conversation, it is never an outcome); any
    /// other stop reason outside this set is a contract violation and throws
    /// <see cref="ChatCompletionException"/> rather than being coerced into a bucket.
    /// </summary>
    public enum ChatCompletionStopReason
    {
        /// <summary>The model finished naturally (<c>end_turn</c>).</summary>
        Complete,

        /// <summary>The output hit the max-tokens ceiling (<c>max_tokens</c>) — flagged, the text is incomplete.</summary>
        Truncated,

        /// <summary>The provider declined the request (<c>refusal</c>); never surfaced as silently empty text.</summary>
        Refused,
    }

    /// <summary>
    /// Typed wrapper for provider failures. <see cref="Retryable"/> is the whole
    /// taxonomy: rate limits, 5xx, and connection failures are retryable; every other
    /// provider error (4xx incl. auth, protocol violations) is not. The original SDK
    /// exception is preserved as <see cref="Exception.InnerException"/>.
    /// </summary>
    public class ChatCompletionException : Exception
    {
        public ChatCompletionException(string message, bool retryable, Exception? innerException = null)
            : base(message, innerException)
        {
            Retryable = retryable;
        }

        /// <summary>True when the caller may reasonably retry the request as-is.</summary>
        public bool Retryable { get; }
    }

    /// <summary>
    /// The tool loop exceeded its <see cref="ChatCompletionToolOptions.MaxToolIterations"/>
    /// cap without the model finishing its answer. Typed so transports can report it
    /// distinctly (the chat middleware maps it to <c>error {code:"tool-loop-limit"}</c>);
    /// non-retryable — retrying the same request would loop the same way.
    /// </summary>
    public sealed class ChatToolLoopLimitException : ChatCompletionException
    {
        public ChatToolLoopLimitException(int maxToolIterations)
            : base($"The completion exceeded the tool-use iteration cap of {maxToolIterations} model turns " +
                   "without finishing an answer.", retryable: false)
        {
            MaxToolIterations = maxToolIterations;
        }

        /// <summary>The cap that was exceeded.</summary>
        public int MaxToolIterations { get; }
    }
}
