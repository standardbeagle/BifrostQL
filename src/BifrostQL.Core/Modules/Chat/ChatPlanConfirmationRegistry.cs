using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>The user's answer to a parked plan proposal.</summary>
    public sealed record ChatPlanDecision(bool Approved, string? Reason);

    /// <summary>
    /// One registered proposal: the single-use id transports resolve against and the
    /// decision task the plan connector parks on. The task completes with the user's
    /// decision, resolves as a DENY on timeout, and cancels when the originating
    /// request is torn down.
    /// </summary>
    public sealed class PendingPlanConfirmation
    {
        internal PendingPlanConfirmation(string confirmationId, Task<ChatPlanDecision> decision)
        {
            ConfirmationId = confirmationId;
            Decision = decision;
        }

        public string ConfirmationId { get; }
        public Task<ChatPlanDecision> Decision { get; }
    }

    /// <summary>
    /// In-process registry of plan proposals awaiting user confirmation — per-node,
    /// like the chat middleware's one-stream-per-conversation guard (a multi-node
    /// deployment needs sticky sessions; the confirmation must reach the node holding
    /// the parked stream). Every entry is:
    ///
    /// <list type="bullet">
    /// <item><b>single-use</b> — resolving removes it atomically; a second resolve of
    /// the same id fails exactly like an unknown id.</item>
    /// <item><b>identity-bound</b> — the resolver's tenant+user must match the
    /// registrant's; a mismatch fails indistinguishably from an unknown id
    /// (fail-closed, no probing).</item>
    /// <item><b>conversation-bound</b> — same rule for the conversation.</item>
    /// <item><b>expiring</b> — the timeout resolves the decision as a DENY (the model
    /// receives a declined result and continues); cancelling the originating request
    /// removes the entry and cancels the decision (stream teardown, nothing written).</item>
    /// </list>
    /// </summary>
    public sealed class ChatPlanConfirmationRegistry
    {
        /// <summary>
        /// The auth-context key a transport stamps the conversation id under when
        /// binding the tool executor, so plan proposals are conversation-bound. The
        /// chat middleware stamps it automatically; a transport that does not cannot
        /// host plan tools (the connector fails fast rather than gating nothing).
        /// </summary>
        public const string ConversationContextKey = "bifrost:chat-conversation";

        private readonly ConcurrentDictionary<string, Entry> _pending = new(StringComparer.Ordinal);

        private sealed class Entry
        {
            public required string IdentityKey { get; init; }
            public required string ConversationKey { get; init; }

            // Async continuations: the confirm endpoint's TrySetResult must never run
            // the parked tool loop inline on the endpoint's request thread.
            public TaskCompletionSource<ChatPlanDecision> Decision { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public CancellationTokenSource? Timeout { get; set; }
            public CancellationTokenRegistration RequestTeardown { get; set; }

            public void ReleaseResources()
            {
                RequestTeardown.Dispose();
                Timeout?.Dispose();
            }
        }

        /// <summary>The number of proposals currently parked (diagnostics/tests).</summary>
        public int PendingCount => _pending.Count;

        /// <summary>
        /// Registers a proposal for the caller identified by
        /// <paramref name="identityKey"/> in <paramref name="conversationKey"/> and
        /// returns its pending handle. The id is cryptographically random; the entry
        /// denies itself after <paramref name="timeout"/> and dies with
        /// <paramref name="requestToken"/>.
        /// </summary>
        public PendingPlanConfirmation Register(
            string identityKey, string conversationKey, TimeSpan timeout, CancellationToken requestToken)
        {
            if (string.IsNullOrWhiteSpace(identityKey))
                throw new ArgumentException("An identity key is required.", nameof(identityKey));
            if (string.IsNullOrWhiteSpace(conversationKey))
                throw new ArgumentException("A conversation key is required.", nameof(conversationKey));
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The confirmation timeout must be positive.");

            var confirmationId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var entry = new Entry { IdentityKey = identityKey, ConversationKey = conversationKey };
            if (!_pending.TryAdd(confirmationId, entry))
                throw new InvalidOperationException("A confirmation id collided; this should be unreachable.");

            // Timeout = DENY: the parked loop receives a declined result and the model
            // continues; nothing is written.
            entry.Timeout = new CancellationTokenSource(timeout);
            entry.Timeout.Token.Register(() =>
            {
                if (TryTake(confirmationId, entry))
                    entry.Decision.TrySetResult(new ChatPlanDecision(
                        false, "The confirmation timed out before the user responded."));
            });

            // Request teardown (client disconnect, stream failure): the id dies with
            // the stream that parked on it.
            entry.RequestTeardown = requestToken.Register(() =>
            {
                if (TryTake(confirmationId, entry))
                    entry.Decision.TrySetCanceled(requestToken);
            });

            return new PendingPlanConfirmation(confirmationId, entry.Decision.Task);
        }

        /// <summary>
        /// Resolves a pending proposal with the user's decision. False — unknown id,
        /// already-used id, identity mismatch, and conversation mismatch alike, so a
        /// transport can answer one indistinguishable 404 — never says which.
        /// </summary>
        public bool TryResolve(
            string confirmationId, string identityKey, string conversationKey, ChatPlanDecision decision)
        {
            if (decision is null)
                throw new ArgumentNullException(nameof(decision));
            if (string.IsNullOrWhiteSpace(confirmationId))
                return false;

            if (!_pending.TryGetValue(confirmationId, out var entry))
                return false;
            if (!string.Equals(entry.IdentityKey, identityKey, StringComparison.Ordinal)
                || !string.Equals(entry.ConversationKey, conversationKey, StringComparison.Ordinal))
                return false;

            // Atomic single-use claim: of a concurrent confirm/deny/timeout, exactly
            // one wins the removal and sets the decision.
            if (!TryTake(confirmationId, entry))
                return false;

            entry.Decision.TrySetResult(decision);
            return true;
        }

        private bool TryTake(string confirmationId, Entry entry)
        {
            if (!_pending.TryRemove(new KeyValuePair<string, Entry>(confirmationId, entry)))
                return false;
            entry.ReleaseResources();
            return true;
        }

        // ---- binding keys -----------------------------------------------------------

        /// <summary>
        /// The identity a proposal is bound to: the caller's tenant + user id from the
        /// auth context (the keys <c>IdentityContextMapper</c> stamps for every
        /// authenticated transport). An auth context with neither is an anonymous or
        /// non-standard identity — fail fast rather than gate a write on nothing.
        /// </summary>
        public static string RequireIdentityKey(IDictionary<string, object?> authContext)
        {
            if (authContext is null)
                throw new ArgumentNullException(nameof(authContext));

            authContext.TryGetValue(MetadataKeys.Auth.DefaultTenantContextKey, out var tenant);
            authContext.TryGetValue(MetadataKeys.Auth.DefaultUserIdContextKey, out var user);
            var tenantKey = Convert.ToString(tenant, CultureInfo.InvariantCulture);
            var userKey = Convert.ToString(user, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(tenantKey) && string.IsNullOrWhiteSpace(userKey))
                throw new InvalidOperationException(
                    "A plan confirmation requires an authenticated identity " +
                    $"('{MetadataKeys.Auth.DefaultTenantContextKey}'/'{MetadataKeys.Auth.DefaultUserIdContextKey}' " +
                    "in the auth context); refusing to gate a write on an anonymous caller.");
            return $"{tenantKey}|{userKey}";
        }

        /// <summary>
        /// The conversation a proposal is bound to, read from the
        /// <see cref="ConversationContextKey"/> the transport stamped. Absent means
        /// the transport did not bind the conversation — plan tools cannot run there.
        /// </summary>
        public static string RequireConversationKey(IDictionary<string, object?> authContext)
        {
            if (authContext is null)
                throw new ArgumentNullException(nameof(authContext));

            if (authContext.TryGetValue(ConversationContextKey, out var value)
                && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } key)
                return key;
            throw new InvalidOperationException(
                "Plan tools require the transport to bind the conversation id into the tool auth context " +
                $"('{ConversationContextKey}'); the chat middleware does this automatically.");
        }

        /// <summary>Canonical string form of a conversation id for binding/resolution.</summary>
        public static string CanonicalConversationKey(object conversationId) =>
            Convert.ToString(conversationId, CultureInfo.InvariantCulture)
            ?? throw new ArgumentException("A conversation id is required.", nameof(conversationId));
    }
}
