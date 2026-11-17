using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using BifrostQL.Core.Utils;
using Microsoft.Extensions.Logging;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// Transport-agnostic core of the native bridge: parses inbound JSON envelopes
    /// (<c>{ id, kind, payload }</c>), routes them to registered handlers, and writes
    /// result/error envelopes back through an injected <c>send</c> delegate. It holds
    /// no reference to Photino, so the full routing, validation, and secret-scrubbing
    /// behaviour is unit-testable without a webview.
    ///
    /// <see cref="NativeBridgeHost"/> is the thin Photino adapter that wires a
    /// <c>PhotinoWindow</c>'s <c>WebMessageReceived</c> event and <c>SendWebMessage</c>
    /// method to a single dispatcher instance.
    /// </summary>
    public sealed class BridgeDispatcher
    {
        /// <summary>
        /// Reserved request kind: <c>{ kind: "cancel", payload: { id } }</c> cancels the
        /// in-flight request <c>id</c>'s token. It gets no reply, and it is handled before
        /// the concurrency cap so it can reach a request that is holding a slot.
        /// </summary>
        public const string CancelKind = "cancel";

        /// <summary>Handlers that may run at once; later requests wait for a slot.</summary>
        public const int DefaultMaxConcurrentRequests = 16;

        private static readonly JsonSerializerOptions DefaultJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly Action<string> _send;
        private readonly JsonSerializerOptions _json;
        private readonly ILogger? _logger;

        private readonly ConcurrentDictionary<
            string,
            Func<JsonElement, CancellationToken, Task<object?>>
        > _handlers = new(StringComparer.Ordinal);

        // Requests whose handler has not finished, by request id, so a cancel envelope
        // (or Clear) can reach their token.
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _slots;

        public BridgeDispatcher(
            Action<string> send,
            JsonSerializerOptions? jsonOptions = null,
            ILogger? logger = null,
            int maxConcurrentRequests = DefaultMaxConcurrentRequests)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _json = jsonOptions ?? DefaultJsonOptions;
            _logger = logger;
            ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentRequests, 1);
            _slots = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
        }

        /// <summary>
        /// Registers an async handler for <paramref name="kind"/>, replacing any
        /// previously registered handler for the same kind.
        /// </summary>
        public void Register(string kind, Func<JsonElement, CancellationToken, Task<object?>> handler)
        {
            if (string.IsNullOrWhiteSpace(kind))
                throw new ArgumentException("Kind must not be empty", nameof(kind));
            ArgumentNullException.ThrowIfNull(handler);
            _handlers[kind] = handler;
        }

        /// <summary>Removes all registered handlers and cancels every in-flight request.</summary>
        public void Clear()
        {
            _handlers.Clear();
            foreach (var cts in _inflight.Values)
                TryCancel(cts);
        }

        /// <summary>
        /// Pushes an unsolicited event envelope to the peer. No response is expected.
        /// </summary>
        public Task SendAsync(string kind, object? payload)
        {
            if (string.IsNullOrWhiteSpace(kind))
                throw new ArgumentException("Kind must not be empty", nameof(kind));

            Emit(new Envelope { Id = Guid.NewGuid().ToString("N"), Kind = kind, Payload = payload });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Parses an inbound envelope, routes it to the matching handler, and writes
        /// the result (or scrubbed error) back through the send delegate. Never throws:
        /// all failures are funnelled to an error envelope or dropped with a log line.
        /// Safe to call concurrently: at most the configured number of handlers run at
        /// once, each with its own token that a <see cref="CancelKind"/> envelope cancels.
        /// </summary>
        /// <summary>
        /// Dispatches <paramref name="message"/> on the thread pool and returns at once:
        /// the entry point for a caller that must not block (Photino's message pump).
        /// Messages are not ordered against each other; a slow handler holds only its
        /// own slot.
        /// </summary>
        public void Post(string message) => _ = Task.Run(() => DispatchAsync(message));

        public async Task DispatchAsync(string message)
        {
            string? requestId = null;
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                {
                    _logger?.LogWarning("NativeBridge: dropped inbound message with missing/invalid id");
                    return;
                }
                requestId = idElement.GetString();

                if (!root.TryGetProperty("kind", out var kindElement) ||
                    kindElement.ValueKind != JsonValueKind.String)
                {
                    SendError(requestId!, "Missing kind");
                    return;
                }
                var kind = kindElement.GetString()!;

                if (kind == CancelKind)
                {
                    CancelRequest(root);
                    return;
                }

                if (!_handlers.TryGetValue(kind, out var handler))
                {
                    SendError(requestId!, $"No handler registered for kind '{kind}'");
                    return;
                }

                // Clone so the handler can outlive the JsonDocument.
                JsonElement payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;

                using var cts = new CancellationTokenSource();
                if (!_inflight.TryAdd(requestId!, cts))
                {
                    SendError(requestId!, "Duplicate request id");
                    return;
                }
                object? result;
                try
                {
                    await _slots.WaitAsync(cts.Token).ConfigureAwait(false);
                    try
                    {
                        result = await handler(payload, cts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _slots.Release();
                    }
                }
                finally
                {
                    _inflight.TryRemove(requestId!, out _);
                }

                SendResult(requestId!, result);
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "NativeBridge: dropped malformed inbound JSON");
            }
            catch (Exception ex) when (requestId is not null)
            {
                // Scrub so a connection-string parse error can't leak a password.
                var scrubbed = BuildScrubbedMessage(ex);
                // No exception object: the default formatter would call ex.ToString(),
                // bypassing the scrubber and leaking driver text into the log.
                _logger?.LogError("NativeBridge: handler for request {Id} threw {Type}: {Message}", requestId, ex.GetType().Name, scrubbed);
                try { SendError(requestId, scrubbed); }
                catch (Exception sendEx) { _logger?.LogError(sendEx, "NativeBridge: failed to send error envelope"); }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "NativeBridge: dispatch failed before id was parsed");
            }
        }

        /// <summary>
        /// Invokes a registered handler directly and returns its result, bypassing the
        /// envelope protocol. Used by transports that already have their own
        /// request/response framing (the HTTP bridge); the webview path keeps using
        /// <see cref="DispatchAsync"/>. Errors are scrubbed identically — a driver
        /// exception can embed the connection string, and that must not reach a client
        /// whichever transport asked.
        /// </summary>
        public async Task<(bool Found, object? Result, string? Error)> InvokeAsync(
            string kind, JsonElement payload, CancellationToken cancellationToken)
        {
            if (!_handlers.TryGetValue(kind, out var handler))
                return (false, null, null);

            try
            {
                var result = await handler(payload, cancellationToken).ConfigureAwait(false);
                return (true, result, null);
            }
            catch (Exception ex)
            {
                var scrubbed = BuildScrubbedMessage(ex);
                _logger?.LogError("NativeBridge: handler for kind {Kind} threw {Type}: {Message}", kind, ex.GetType().Name, scrubbed);
                return (true, null, scrubbed);
            }
        }

        private void CancelRequest(JsonElement root)
        {
            if (root.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("id", out var target)
                && target.ValueKind == JsonValueKind.String
                && _inflight.TryGetValue(target.GetString()!, out var cts))
            {
                TryCancel(cts);
            }
            // A cancel for a request that already finished (or never existed) is normal:
            // the webview sends it after its own timeout, racing the reply.
        }

        private static void TryCancel(CancellationTokenSource cts)
        {
            // The owning dispatch disposes it on completion, possibly mid-cancel.
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void SendResult(string id, object? payload)
            => Emit(new Envelope { Id = id, Kind = "result", Payload = payload });

        private void SendError(string id, string message)
            => Emit(new Envelope { Id = id, Kind = "error", Payload = new { message } });

        private void Emit(Envelope envelope) => _send(JsonSerializer.Serialize(envelope, _json));

        /// <summary>
        /// Joins the exception chain's messages and runs the whole thing through
        /// <see cref="SecretScrubber"/> so inner messages stay debuggable without
        /// leaking secrets (the scrubber runs last).
        /// </summary>
        private static string BuildScrubbedMessage(Exception ex)
        {
            var parts = new List<string>();
            var current = ex;
            while (current is not null)
            {
                if (!string.IsNullOrEmpty(current.Message))
                    parts.Add(current.Message);
                current = current.InnerException;
            }
            return SecretScrubber.Scrub(string.Join(" -> ", parts)) ?? string.Empty;
        }

        private sealed class Envelope
        {
            [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
            [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
            [JsonPropertyName("payload")] public object? Payload { get; set; }
        }
    }
}
