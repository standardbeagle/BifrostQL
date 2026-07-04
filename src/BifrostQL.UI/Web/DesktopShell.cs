using System.Text.Json;
using BifrostQL.UI.NativeBridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photino.NET;

namespace BifrostQL.UI.Web
{
    /// <summary>
    /// Owns the Photino desktop window lifecycle: creates the window, wires the
    /// in-process native bridge (credentials, raw SQL, visual query builder), blocks
    /// until the window closes, then tears down the SSH tunnel and web host.
    ///
    /// The native bridge is deliberately non-HTTP — messages ride the Photino webview
    /// IPC so credentials and host-only features never traverse localhost sockets.
    /// This separation is a security boundary and must be preserved.
    /// </summary>
    public static class DesktopShell
    {
        public static async Task RunAsync(WebApplication app, string localUrl, ConnectionState state, SshTunnelManager sshTunnel)
        {
            // DevTools and the WebView context menu are gated to Development so
            // Release builds never expose F12 / right-click-Inspect on the embedded
            // React SPA (protects JS heap + source maps).
            var isDev = app.Environment.IsDevelopment();
            var window = new PhotinoWindow()
                .SetTitle("BifrostQL - Database Explorer")
                .SetSize(1400, 900)
                .Center()
                .SetDevToolsEnabled(isDev)
                .SetContextMenuEnabled(isDev)
                .Load(localUrl);

            var bridgeLogger = app.Services
                .GetService<ILoggerFactory>()?
                .CreateLogger<NativeBridgeHost>();
            using var nativeBridge = new NativeBridgeHost(window, bridgeLogger);

            // Smoke-test handler for the wire format: echo the raw JSON back so the
            // caller can confirm its payload round-tripped unchanged. GetRawText keeps
            // us agnostic to payload shape — primitives, objects, null all fall out
            // the same way.
            nativeBridge.Register("ping", (payload, _) =>
            {
                var echo = payload.ValueKind == JsonValueKind.Undefined
                    ? "undefined"
                    : payload.GetRawText();
                return Task.FromResult<object?>(new { pong = true, echo });
            });

            new VaultBridgeHandlers(window, bridgeLogger, state.VaultPath).Register(nativeBridge);
            new RawSqlBridgeHandler(state).Register(nativeBridge);
            new VisualQueryBridgeHandlers(state, app.Services).Register(nativeBridge);

            window.WaitForClose();

            // Shutdown the server and SSH tunnel when the window closes
            await sshTunnel.DisposeAsync();
            await app.StopAsync();
        }
    }
}
