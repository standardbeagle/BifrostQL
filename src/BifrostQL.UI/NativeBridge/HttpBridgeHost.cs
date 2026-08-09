using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// An opt-in loopback HTTP transport for the bridge handlers, so the editor's
    /// desktop-only panes (raw SQL console, visual query builder, form builder) can be
    /// driven when the app runs headless — i.e. by the end-to-end suite, which has no
    /// webview and therefore no <c>window.external</c>.
    ///
    /// <para><b>This is not a product surface.</b> The desktop bridge deliberately never
    /// touches the network: it runs SQL against the host's active connection with no
    /// authentication of its own, because in Photino the only possible caller is the
    /// window the host itself opened. Putting that on a socket removes the property the
    /// design rests on, so it is <b>off unless explicitly enabled</b>
    /// (<c>--enable-http-bridge</c>), binds only where the UI host already binds
    /// (loopback), and logs a warning at startup. Never enable it on a shared or
    /// reachable host.</para>
    ///
    /// <para>The handlers are the SAME instances the Photino channel registers, so a
    /// test driving this transport exercises the shipped logic rather than a
    /// re-implementation of it.</para>
    /// </summary>
    public sealed class HttpBridgeHost : IBridgeRegistry
    {
        /// <summary>Route the browser posts bridge requests to, as <c>{prefix}/{kind}</c>.</summary>
        public const string RoutePrefix = "/_bridge";

        private readonly BridgeDispatcher _dispatcher;

        public HttpBridgeHost(ILogger? logger = null)
        {
            // No peer to push to: this transport answers on the HTTP response, so the
            // envelope send delegate is never used.
            _dispatcher = new BridgeDispatcher(_ => { }, jsonOptions: null, logger);
        }

        /// <inheritdoc />
        public void Register(string kind, Func<JsonElement, CancellationToken, Task<object?>> handler)
            => _dispatcher.Register(kind, handler);

        /// <summary>
        /// Runs the handler for <paramref name="kind"/>. <c>Found</c> is false when no
        /// handler is registered, which the endpoint maps to 404 — an unregistered kind
        /// is a caller error, not a server fault.
        /// </summary>
        public Task<(bool Found, object? Result, string? Error)> InvokeAsync(
            string kind, JsonElement payload, CancellationToken cancellationToken)
            => _dispatcher.InvokeAsync(kind, payload, cancellationToken);
    }
}
