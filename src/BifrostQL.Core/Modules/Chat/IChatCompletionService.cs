using System;
using System.Collections.Generic;
using System.Threading;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// The chat module's single narrow seam onto the LLM. Implementations stream a
    /// completion for an ordered message history: zero or more
    /// <see cref="ChatCompletionDelta"/> text deltas followed by exactly one terminal
    /// <see cref="ChatCompletionResult"/>. Nothing else in Core touches the provider
    /// SDK's wire types — callers consume this contract only.
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
    }

    /// <summary>Base of the two stream event shapes; consumers switch on the concrete type.</summary>
    public abstract record ChatCompletionEvent;

    /// <summary>One incremental chunk of assistant text.</summary>
    public sealed record ChatCompletionDelta(string Text) : ChatCompletionEvent;

    /// <summary>
    /// The terminal completion record — always the last event of a successful stream.
    /// <see cref="FullText"/> is exactly the concatenation of the preceding deltas.
    /// </summary>
    /// <param name="FullText">The complete assistant text (empty on a pre-output refusal).</param>
    /// <param name="StopReason">The typed outcome; see <see cref="ChatCompletionStopReason"/>.</param>
    /// <param name="RefusalCategory">
    /// The provider's refusal category (e.g. "cyber") when <see cref="StopReason"/> is
    /// <see cref="ChatCompletionStopReason.Refused"/> and the provider reported one; null otherwise.
    /// </param>
    /// <param name="InputTokens">Prompt tokens billed for the request.</param>
    /// <param name="OutputTokens">Completion tokens billed for the response.</param>
    public sealed record ChatCompletionResult(
        string FullText,
        ChatCompletionStopReason StopReason,
        string? RefusalCategory,
        long InputTokens,
        long OutputTokens) : ChatCompletionEvent;

    /// <summary>
    /// The closed outcome taxonomy. A stop reason outside this set (e.g. a tool-use stop
    /// from a request shape this service never sends) is a contract violation and throws
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
    public sealed class ChatCompletionException : Exception
    {
        public ChatCompletionException(string message, bool retryable, Exception? innerException = null)
            : base(message, innerException)
        {
            Retryable = retryable;
        }

        /// <summary>True when the caller may reasonably retry the request as-is.</summary>
        public bool Retryable { get; }
    }
}
