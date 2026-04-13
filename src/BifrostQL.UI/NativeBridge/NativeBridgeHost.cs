using System.Collections.Concurrent;
using System.Text.Json;
using BifrostQL.Core.Utils;
using Microsoft.Extensions.Logging;
using Photino.NET;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// Host-side counterpart to <c>src/BifrostQL.UI/frontend/src/lib/native-bridge.ts</c>.
    ///
    /// Wraps a <see cref="PhotinoWindow"/> and turns its single-stream
    /// <c>WebMessageReceived</c> event + <c>SendWebMessage</c> method into a
    /// typed request/response and push-event channel. Messages are JSON
    /// envelopes of the form <c>{ id, kind, payload }</c> — the same wire
    /// format the TypeScript bridge emits and expects.
    ///
    /// Design notes:
    ///
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>In-process only.</b> The bridge deliberately avoids HTTP; all
    ///     traffic rides the Photino webview IPC channel so credentials,
    ///     secrets, and native-only features never touch localhost network
    ///     sockets that another process could observe.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Handler isolation.</b> Exceptions thrown by registered handlers
    ///     are caught, the message is run through
    ///     <see cref="SecretScrubber.Scrub"/> (so password-laden connection
    ///     strings never leak to the webview JS heap), and an
    ///     <c>{ kind: "error" }</c> envelope is returned to the matching
    ///     request id. The host loop keeps running.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Thread safety.</b> Registrations use
    ///     <see cref="ConcurrentDictionary{TKey, TValue}"/> so callers can
    ///     register handlers during startup from any thread. Photino raises
    ///     <c>WebMessageReceived</c> on its message-pump thread; dispatch is
    ///     <see langword="async void"/>-free and bridges to Task-returning
    ///     handlers via a fire-and-forget <c>_ = DispatchAsync(...)</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Lifetime.</b> <see cref="Dispose"/> unsubscribes from
    ///     <c>WebMessageReceived</c> so that the host can be torn down
    ///     independently of the window without dangling event references.
    ///     Dispose is idempotent.
    ///   </description></item>
    /// </list>
    /// </summary>
    public sealed class NativeBridgeHost : IDisposable
    {
        // Matches the TS BridgeRequestOptions default. The value is shared
        // between client and server purely by convention — the wire protocol
        // itself is stateless.
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly PhotinoWindow _window;
        private readonly ILogger<NativeBridgeHost>? _logger;
        private readonly EventHandler<string> _handler;

        private readonly ConcurrentDictionary<
            string,
            Func<JsonElement, CancellationToken, Task<object?>>
        > _handlers = new(StringComparer.Ordinal);

        private int _disposed;

        /// <summary>
        /// Wires up a new host. Subscribes to
        /// <see cref="PhotinoWindow.WebMessageReceived"/> immediately; call
        /// <see cref="Dispose"/> to unsubscribe.
        /// </summary>
        public NativeBridgeHost(
            PhotinoWindow window,
            ILogger<NativeBridgeHost>? logger = null)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _logger = logger;

            // Cache the delegate so we can pass the exact same reference to
            // -= in Dispose. Anonymous lambdas would allocate a fresh
            // delegate each time, making unsubscription a no-op.
            _handler = OnWebMessageReceived;
            _window.WebMessageReceived += _handler;
        }

        /// <summary>
        /// Registers an async handler for the given <paramref name="kind"/>.
        /// Replaces any previously registered handler for the same kind so
        /// test setups and re-initialization flows don't accumulate ghosts.
        /// </summary>
        public void Register(
            string kind,
            Func<JsonElement, CancellationToken, Task<object?>> handler)
        {
            if (string.IsNullOrWhiteSpace(kind))
                throw new ArgumentException("Kind must not be empty", nameof(kind));
            ArgumentNullException.ThrowIfNull(handler);
            _handlers[kind] = handler;
        }

        /// <summary>
        /// Pushes an unsolicited event to the webview. The webview side
        /// dispatches via <c>onBridgeEvent(kind, handler)</c>; there is no
        /// response expected for these messages.
        /// </summary>
        public Task SendAsync(string kind, object? payload)
        {
            if (string.IsNullOrWhiteSpace(kind))
                throw new ArgumentException("Kind must not be empty", nameof(kind));

            var envelope = new Envelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                Payload = payload
            };
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            _window.SendWebMessage(json);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Event bridge from Photino's sync event to our async dispatch. We
        /// intentionally swallow the returned Task on the fire-and-forget
        /// path: all failures are caught inside
        /// <see cref="DispatchAsync"/> and flowed back to the webview as an
        /// <c>error</c> envelope, so nothing can escape the event handler
        /// into Photino's message pump.
        /// </summary>
        private void OnWebMessageReceived(object? sender, string message)
        {
            _ = DispatchAsync(message);
        }

        /// <summary>
        /// Parses an inbound envelope, routes it to the matching handler,
        /// and writes the result (or scrubbed error) back to the webview.
        /// </summary>
        private async Task DispatchAsync(string message)
        {
            string? requestId = null;
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                {
                    _logger?.LogWarning(
                        "NativeBridge: dropped inbound message with missing/invalid id");
                    return;
                }
                requestId = idElement.GetString();

                if (!root.TryGetProperty("kind", out var kindElement) ||
                    kindElement.ValueKind != JsonValueKind.String)
                {
                    await SendErrorAsync(requestId!, "Missing kind");
                    return;
                }
                var kind = kindElement.GetString()!;

                if (!_handlers.TryGetValue(kind, out var handler))
                {
                    await SendErrorAsync(
                        requestId!,
                        $"No handler registered for kind '{kind}'");
                    return;
                }

                // Clone the payload element so handlers can outlive the
                // JsonDocument without risking use-after-dispose.
                JsonElement payload = root.TryGetProperty("payload", out var p)
                    ? p.Clone()
                    : default;

                var result = await handler(payload, CancellationToken.None)
                    .ConfigureAwait(false);

                await SendResultAsync(requestId!, result);
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(
                    ex, "NativeBridge: dropped malformed inbound JSON");
                // No id to respond against — just drop. Matches TS
                // dispatcher's malformed-inbound behavior.
            }
            catch (Exception ex) when (requestId is not null)
            {
                // All handler exceptions funnel here. Scrub through the
                // SecretScrubber so a connection-string parsing error
                // doesn't leak a password via ex.Message.
                var scrubbed = BuildScrubbedMessage(ex);
                _logger?.LogError(
                    ex, "NativeBridge: handler for request {Id} threw", requestId);
                try
                {
                    await SendErrorAsync(requestId, scrubbed);
                }
                catch (Exception sendEx)
                {
                    _logger?.LogError(
                        sendEx, "NativeBridge: failed to send error envelope");
                }
            }
            catch (Exception ex)
            {
                // No request id to reply to — at least log it.
                _logger?.LogError(
                    ex, "NativeBridge: dispatch failed before id was parsed");
            }
        }

        private Task SendResultAsync(string id, object? payload)
        {
            var envelope = new Envelope
            {
                Id = id,
                Kind = "result",
                Payload = payload
            };
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            _window.SendWebMessage(json);
            return Task.CompletedTask;
        }

        private Task SendErrorAsync(string id, string message)
        {
            var envelope = new Envelope
            {
                Id = id,
                Kind = "error",
                Payload = new { message }
            };
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            _window.SendWebMessage(json);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Walks the exception chain collecting each layer's message, runs
        /// the whole thing through <see cref="SecretScrubber"/>, and returns
        /// the result. Keeping inner messages preserves debuggability
        /// without risking leaked secrets because the scrubber runs last.
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
            var joined = string.Join(" -> ", parts);
            return SecretScrubber.Scrub(joined) ?? string.Empty;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _window.WebMessageReceived -= _handler;
            _handlers.Clear();
        }

        // Private serialization target. Using a POCO (rather than an
        // anonymous type) gives us stable camelCase field order across the
        // whole file so both ends parse deterministically. The attributes
        // are redundant with JsonNamingPolicy.CamelCase but explicit is
        // cheap insurance against future JsonOptions drift.
        private sealed class Envelope
        {
            [System.Text.Json.Serialization.JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [System.Text.Json.Serialization.JsonPropertyName("kind")]
            public string Kind { get; set; } = string.Empty;

            [System.Text.Json.Serialization.JsonPropertyName("payload")]
            public object? Payload { get; set; }
        }
    }
}
