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
        /// Overrides whether authentication is required to access the endpoint. Null (the
        /// default) inherits the requirement of the GraphQL endpoint the overlay is filtered
        /// against (<see cref="GraphQlEndpoint"/>) through <c>MountAuthRequirement</c>, the one
        /// derivation every HTTP mount uses: required unless that endpoint sets
        /// <c>DisableAuth</c>, and required whenever the endpoint cannot be identified (none
        /// registered, or several with none named).
        ///
        /// <para>The overlay enumerates entities, fields, grid columns and relationships — an
        /// introspection surface — and is always narrowed to what the caller may read. Set this
        /// only to state a posture the endpoint configuration cannot express: <c>true</c> to
        /// keep the overlay closed in front of an anonymous GraphQL endpoint, <c>false</c> to
        /// open it in front of an authenticated one.</para>
        /// </summary>
        public bool? RequireAuth { get; set; }

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
    /// <para>Being introspection, it is gated like one: the auth requirement is the served
    /// endpoint's, derived once at mount time by <c>MountAuthRequirement</c> and passed in as a
    /// required constructor argument (fail closed when the endpoint cannot be identified), identity is projected through the
    /// shared <see cref="IBifrostAuthContextFactory"/>, and the served overlay is narrowed to the
    /// entities and fields that caller may READ under the same policy evaluator the query path
    /// enforces (.claude/rules/protocol-adapter-security.md invariant 4).</para>
    /// </summary>
    public sealed class BifrostAppMetadataMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly BifrostAppMetadataOptions _options;
        private readonly bool _requiresAuth;

        /// <param name="next">The next middleware.</param>
        /// <param name="options">Endpoint options.</param>
        /// <param name="requiresAuth">
        /// Whether a caller must carry a projected identity. <c>UseBifrostAppMetadata</c> derives it
        /// from <c>MountAuthRequirement</c> (or the explicit <see cref="BifrostAppMetadataOptions.RequireAuth"/>
        /// override); it is required here so no construction path can default it.
        /// </param>
        public BifrostAppMetadataMiddleware(RequestDelegate next, BifrostAppMetadataOptions options, bool requiresAuth)
        {
            _next = next;
            _options = options;
            _requiresAuth = requiresAuth;
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

            // Identity is projected ONCE, up front, through the shared gate — before any overlay is
            // loaded — and refused by the same rule /_saved-objects and /_blob use. IsAuthenticated
            // alone is not an identity: an authenticated principal that projects to an EMPTY
            // context used to be served the overlay here while both siblings answered 401.
            var outcome = BifrostIdentityGate.Project(context, out var userContext);
            if (BifrostIdentityGate.Refuses(outcome, _requiresAuth))
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
        /// the overlay is served as authored. That case is guarded by the auth requirement alone:
        /// with no Bifrost endpoint registered, <c>MountAuthRequirement</c> resolves it to
        /// "required". If the model IS registered but cannot be
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
