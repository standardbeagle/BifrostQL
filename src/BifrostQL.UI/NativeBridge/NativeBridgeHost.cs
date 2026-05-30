using System.Text.Json;
using Microsoft.Extensions.Logging;
using Photino.NET;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// Host-side counterpart to <c>src/BifrostQL.UI/frontend/src/lib/native-bridge.ts</c>.
    ///
    /// A thin Photino adapter: it wires a <see cref="PhotinoWindow"/>'s single-stream
    /// <c>WebMessageReceived</c> event and <c>SendWebMessage</c> method to a
    /// <see cref="BridgeDispatcher"/>, which owns all envelope parsing, routing, and
    /// secret-scrubbing. Messages are JSON envelopes of the form
    /// <c>{ id, kind, payload }</c> — the same wire format the TypeScript bridge emits.
    ///
    /// Design notes:
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>In-process only.</b> All traffic rides the Photino webview IPC channel so
    ///     credentials and native-only features never touch localhost network sockets.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Handler isolation.</b> Handler exceptions are caught by the dispatcher,
    ///     scrubbed via <c>SecretScrubber</c>, and returned as an <c>error</c> envelope;
    ///     the host loop keeps running.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Lifetime.</b> <see cref="Dispose"/> unsubscribes from
    ///     <c>WebMessageReceived</c> and is idempotent.
    ///   </description></item>
    /// </list>
    /// </summary>
    public sealed class NativeBridgeHost : IDisposable
    {
        private readonly PhotinoWindow _window;
        private readonly EventHandler<string> _handler;
        private readonly BridgeDispatcher _dispatcher;

        private int _disposed;

        /// <summary>
        /// Wires up a new host. Subscribes to
        /// <see cref="PhotinoWindow.WebMessageReceived"/> immediately; call
        /// <see cref="Dispose"/> to unsubscribe.
        /// </summary>
        public NativeBridgeHost(PhotinoWindow window, ILogger<NativeBridgeHost>? logger = null)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _dispatcher = new BridgeDispatcher(_window.SendWebMessage, jsonOptions: null, logger);

            // Cache the delegate so we pass the exact same reference to -= in Dispose;
            // an anonymous lambda would make unsubscription a no-op.
            _handler = OnWebMessageReceived;
            _window.WebMessageReceived += _handler;
        }

        /// <summary>
        /// Registers an async handler for the given <paramref name="kind"/>, replacing
        /// any previously registered handler for the same kind.
        /// </summary>
        public void Register(string kind, Func<JsonElement, CancellationToken, Task<object?>> handler)
            => _dispatcher.Register(kind, handler);

        /// <summary>
        /// Pushes an unsolicited event to the webview (dispatched via
        /// <c>onBridgeEvent(kind, handler)</c> on the JS side; no response expected).
        /// </summary>
        public Task SendAsync(string kind, object? payload) => _dispatcher.SendAsync(kind, payload);

        // Photino raises this on its message-pump thread. Dispatch is fire-and-forget:
        // all failures are caught inside the dispatcher and flowed back as an error
        // envelope, so nothing escapes into Photino's pump.
        private void OnWebMessageReceived(object? sender, string message) => _ = _dispatcher.DispatchAsync(message);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _window.WebMessageReceived -= _handler;
            _dispatcher.Clear();
        }
    }
}
