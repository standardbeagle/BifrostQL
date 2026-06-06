using System.Text;
using BifrostQL.Core.AppMetadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server
{
    /// <summary>
    /// Configuration for the BifrostQL app-metadata overlay endpoint. The
    /// endpoint serves the loaded <see cref="AppMetadataModel"/> as the stable
    /// camelCase JSON contract defined by <see cref="AppMetadataJson"/>, ready
    /// for consumption by SPA and React Native clients.
    /// </summary>
    public sealed class BifrostAppMetadataOptions
    {
        /// <summary>
        /// Whether the app-metadata endpoint is enabled. Default: true.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// The path for the app-metadata endpoint. Default: "/_app-metadata".
        /// </summary>
        public string Path { get; set; } = "/_app-metadata";

        /// <summary>
        /// Whether authentication is required to access the endpoint.
        /// Default: false.
        /// </summary>
        public bool RequireAuth { get; set; }
    }

    /// <summary>
    /// Middleware that serves the app-metadata overlay as JSON on a GET
    /// endpoint, following the same pattern as <see cref="BifrostInfoMiddleware"/>.
    ///
    /// The overlay is exposed verbatim using the stable camelCase contract
    /// (<see cref="AppMetadataJson"/>) — the same contract sub-task 1 defined —
    /// so SPA and React Native clients consume one portable JSON shape. This
    /// endpoint is the app-metadata counterpart of the GraphQL introspection
    /// the schema-metadata system already exposes; the two coexist and neither
    /// is merged into the other.
    /// </summary>
    public sealed class BifrostAppMetadataMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly BifrostAppMetadataOptions _options;

        public BifrostAppMetadataMiddleware(RequestDelegate next, BifrostAppMetadataOptions options)
        {
            _next = next;
            _options = options;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!_options.Enabled
                || !HttpMethods.IsGet(context.Request.Method)
                || !string.Equals(context.Request.Path.Value, _options.Path, StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }

            if (_options.RequireAuth && !(context.User?.Identity?.IsAuthenticated ?? false))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // An empty overlay is served when none is registered, so the
            // endpoint always returns the stable contract rather than 404.
            var overlay = context.RequestServices.GetService<Lazy<Task<AppMetadataModel>>>();
            var model = overlay != null ? await overlay.Value : new AppMetadataModel();
            var json = AppMetadataJson.Serialize(model);

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(json), context.RequestAborted);
        }
    }
}
