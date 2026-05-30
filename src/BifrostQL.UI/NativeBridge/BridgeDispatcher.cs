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

        public BridgeDispatcher(Action<string> send, JsonSerializerOptions? jsonOptions = null, ILogger? logger = null)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _json = jsonOptions ?? DefaultJsonOptions;
            _logger = logger;
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

        /// <summary>Removes all registered handlers.</summary>
        public void Clear() => _handlers.Clear();

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
        /// </summary>
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

                if (!_handlers.TryGetValue(kind, out var handler))
                {
                    SendError(requestId!, $"No handler registered for kind '{kind}'");
                    return;
                }

                // Clone so the handler can outlive the JsonDocument.
                JsonElement payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;

                var result = await handler(payload, CancellationToken.None).ConfigureAwait(false);

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
                _logger?.LogError(ex, "NativeBridge: handler for request {Id} threw", requestId);
                try { SendError(requestId, scrubbed); }
                catch (Exception sendEx) { _logger?.LogError(sendEx, "NativeBridge: failed to send error envelope"); }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "NativeBridge: dispatch failed before id was parsed");
            }
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
