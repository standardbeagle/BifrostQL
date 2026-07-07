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

        // Serializes message handling. Photino raises WebMessageReceived on its pump
        // thread and we must not block it, but handlers mutate shared ConnectionState
        // (e.g. vault-connect swaps _state.ConnectionString while exec-sql reads it),
        // so unordered fire-and-forget dispatch could interleave those. We instead
        // chain each incoming message onto a single running task: messages are handled
        // one at a time, in arrival order, off the pump thread. Access to _pump is
        // guarded by _pumpLock; the pump thread only ever does a cheap chaining append.
        private readonly object _pumpLock = new();
        private Task _pump = Task.CompletedTask;

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

        // Photino raises this on its message-pump thread. We never block the pump:
        // the message is appended to the serial _pump chain so it runs off-thread,
        // after any earlier message has finished. This gives arrival-order handling
        // and natural backpressure without a queue of unbounded concurrent tasks.
        // All handler failures are caught inside the dispatcher and flowed back as an
        // error envelope, so nothing escapes into Photino's pump — including a slow or
        // faulted predecessor, which must not stall the whole chain (ContinueWith runs
        // regardless of the previous task's outcome).
        private void OnWebMessageReceived(object? sender, string message)
        {
            lock (_pumpLock)
            {
                _pump = _pump.ContinueWith(
                    _ => _dispatcher.DispatchAsync(message),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _window.WebMessageReceived -= _handler;
            _dispatcher.Clear();
        }
    }
}
