using System.Text;
using BifrostQL.Core.AppMetadata;
using BifrostQL.Core.Resolvers;
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
        /// Default: <c>true</c>.
        ///
        /// <para>The overlay enumerates entities, fields, grid columns and relationships — an
        /// introspection surface, not a public asset. Every other Bifrost transport gate requires
        /// identity and narrows what it returns from it, so this one does too. Setting it to
        /// <c>false</c> publishes the whole overlay to anonymous callers and is a deliberate
        /// deployment decision, never the default.</para>
        /// </summary>
        public bool RequireAuth { get; set; } = true;

        /// <summary>
        /// The registered GraphQL endpoint path whose cached model the overlay is filtered
        /// against. Null selects the single registered endpoint.
        /// </summary>
        public string? GraphQlEndpoint { get; set; }
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
    ///
    /// <para>Being introspection, it is gated like one: authentication is required by default
    /// (<see cref="BifrostAppMetadataOptions.RequireAuth"/>), identity is projected through the
    /// shared <see cref="IBifrostAuthContextFactory"/>, and the served overlay is narrowed to the
    /// entities and fields that caller may READ under the same policy evaluator the query path
    /// enforces (.claude/rules/protocol-adapter-security.md invariant 4).</para>
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

            // Identity is projected ONCE, up front, through the shared gate — before any overlay is
            // loaded — so an identity this deployment cannot project is refused with 401 rather than
            // faulting mid-response. The sibling /_saved-objects gate has always answered 401 here;
            // this endpoint used to let the fault escape to the host (invariant 1's shape), and the
            // two now share one implementation so they cannot diverge again.
            var outcome = BifrostIdentityGate.Project(context, out var userContext);
            if (outcome == BifrostIdentityOutcome.Unprojectable)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // An empty overlay is served when none is registered, so the
            // endpoint always returns the stable contract rather than 404.
            var overlay = context.RequestServices.GetService<AppMetadataCache>();
            var model = overlay != null ? await overlay.GetAsync() : new AppMetadataModel();

            model = await FilterForCallerAsync(context, model, userContext);

            var json = AppMetadataJson.Serialize(model);

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(json), context.RequestAborted);
        }

        /// <summary>
        /// Narrows the overlay to what THIS caller may read, from the <paramref name="userContext"/>
        /// already projected through the shared <see cref="BifrostIdentityGate"/> — the same
        /// <see cref="IBifrostAuthContextFactory"/> seam every other transport gate uses, so the
        /// projection cannot drift — applied by <see cref="AppMetadataVisibility"/> using the
        /// evaluator the query path calls (.claude/rules/protocol-adapter-security.md invariant 4).
        ///
        /// <para>Filtering needs a schema model to authorize against. When no
        /// <see cref="IQueryIntentExecutor"/> is registered, this process hosts no Bifrost data
        /// path at all — there is no table whose readability the overlay could contradict — so
        /// the overlay is served as authored. That case is guarded by <see cref="BifrostAppMetadataOptions.RequireAuth"/>
        /// alone, which is why it defaults on. If the model IS registered but cannot be
        /// resolved, the request fails rather than silently degrading to an unfiltered
        /// overlay.</para>
        /// </summary>
        private async Task<AppMetadataModel> FilterForCallerAsync(
            HttpContext context, AppMetadataModel overlay, IDictionary<string, object?> userContext)
        {
            if (overlay.Entities.Count == 0)
                return overlay;

            var reads = context.RequestServices.GetService<IQueryIntentExecutor>();
            if (reads is null)
                return overlay;

            var model = await reads.GetModelAsync(_options.GraphQlEndpoint);
            return AppMetadataVisibility.Project(overlay, model, userContext);
        }
    }
}
