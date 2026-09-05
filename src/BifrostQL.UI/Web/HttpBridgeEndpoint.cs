using System.Text.Json;
using BifrostQL.UI.NativeBridge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BifrostQL.UI.Web
{
    /// <summary>
    /// Maps the opt-in loopback HTTP transport for the desktop bridge
    /// (<c>--enable-http-bridge</c>). See <see cref="HttpBridgeHost"/> for why this is
    /// a testing affordance and not a product surface.
    ///
    /// <para>Two routes: <c>GET /_bridge</c> is the probe the client uses to decide
    /// whether the desktop-only panes are reachable, and <c>POST /_bridge/{kind}</c>
    /// invokes a handler with the request body as its payload.</para>
    /// </summary>
    public static class HttpBridgeEndpoint
    {
        /// <summary>Preflight-forcing header every bridge POST must carry.</summary>
        public const string BridgeHeaderName = "X-Bifrost-Bridge";
        public const string BridgeHeaderValue = "1";

        public static void Map(WebApplication app, ConnectionState state)
        {
            var logger = app.Services.GetService<ILoggerFactory>()?.CreateLogger("BifrostQL.UI.HttpBridge");
            var bridge = new HttpBridgeHost(logger);

            // The vault and save-file handlers are deliberately absent: they drive
            // native windows (credential prompts, the OS save dialog), which do not
            // exist headless — and the vault ones additionally mutate stored secrets.
            new RawSqlBridgeHandler(state).Register(bridge);
            new VisualQueryBridgeHandlers(state, app.Services).Register(bridge);
            new SchemaDdlBridgeHandler(state, app.Services).Register(bridge);

            app.MapGet(HttpBridgeHost.RoutePrefix, () => Results.Ok(new { enabled = true }));

            app.MapPost($"{HttpBridgeHost.RoutePrefix}/{{kind}}", async (
                string kind, HttpRequest request, CancellationToken cancellationToken) =>
            {
                // CSRF guard: a cross-origin no-cors POST can send text/plain but
                // can never set a custom header — the header forces a preflight this
                // host never grants. Both are required before the body is touched.
                if (!request.Headers.TryGetValue(BridgeHeaderName, out var marker) || marker != BridgeHeaderValue)
                    return Results.Json(
                        new { message = $"Missing {BridgeHeaderName} header." },
                        statusCode: StatusCodes.Status403Forbidden);
                if (request.ContentLength is > 0 &&
                    !string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                    return Results.Json(
                        new { message = "Content-Type must be application/json." },
                        statusCode: StatusCodes.Status415UnsupportedMediaType);

                JsonElement payload = default;
                if (request.ContentLength is > 0)
                {
                    try
                    {
                        using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                        payload = doc.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        return Results.BadRequest(new { message = "Body must be JSON." });
                    }
                }

                var (found, result, error) = await bridge.InvokeAsync(kind, payload, cancellationToken);
                if (!found)
                    return Results.NotFound(new { message = $"No handler registered for kind '{kind}'." });
                // The message is already scrubbed by the dispatcher — a driver exception
                // can carry the connection string, and this transport is over a socket.
                return error is not null
                    ? Results.Json(new { message = error }, statusCode: StatusCodes.Status500InternalServerError)
                    : Results.Json(new { result });
            });
        }
    }
}
