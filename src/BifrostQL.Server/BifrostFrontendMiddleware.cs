using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using GraphQL;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server
{
    /// <summary>
    /// ASP.NET Core middleware that dispatches requests to a registered IProtocolFrontend.
    /// Routes by matching the request's Content-Type header to a frontend's ContentType.
    /// Falls through to the next middleware if no frontend matches.
    ///
    /// <para>The mount lives in its own <c>Map</c> branch, so the per-endpoint gate
    /// <c>UseBifrostEndpoints</c> installs INSIDE the GraphQL branch never covers it: this
    /// middleware runs its own fail-closed identity gate, through the shared
    /// <see cref="BifrostIdentityGate"/>, before the request body is read.</para>
    /// </summary>
    public sealed class BifrostFrontendMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IProtocolFrontend _frontend;
        private readonly IBifrostEngine _engine;
        private readonly string _endpointPath;

        /// <summary>
        /// Whether this mount requires an authenticated caller. Resolved from the endpoint whose
        /// schema the mount serves (see <see cref="FrontendExtensions.UseBifrostFrontend"/>) so
        /// transport and endpoint cannot state different postures, and a REQUIRED constructor
        /// argument rather than a defaulted one: a construction site that has not thought about
        /// posture must not be able to inherit "anonymous" by omission.
        /// </summary>
        private readonly bool _requireAuthenticatedIdentity;

        public BifrostFrontendMiddleware(
            RequestDelegate next,
            IProtocolFrontend frontend,
            IBifrostEngine engine,
            string endpointPath,
            bool requireAuthenticatedIdentity)
        {
            _next = next;
            _frontend = frontend ?? throw new ArgumentNullException(nameof(frontend));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _endpointPath = endpointPath ?? throw new ArgumentNullException(nameof(endpointPath));
            _requireAuthenticatedIdentity = requireAuthenticatedIdentity;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!IsMatch(context.Request))
            {
                await _next(context);
                return;
            }

            // Fail-closed identity gate, BEFORE the body is read and before the engine is
            // reached. Projecting the caller with CreateUserContext alone is NOT a gate: an
            // unauthenticated caller projects to an EMPTY user context, which only scopes away
            // tables that DECLARE tenant metadata — every other table stayed readable by a
            // caller who presented nothing. The refusal states only that authentication failed:
            // it does not vary with what exists behind the mount, and carries no exception text
            // (.claude/rules/protocol-adapter-security.md invariant 3).
            var outcome = BifrostIdentityGate.Project(context, out var identityContext);
            if (outcome == BifrostIdentityOutcome.Unprojectable)
            {
                // Token from an OIDC issuer this deployment mapped nothing for. Answered here,
                // never allowed to escape to the host, and never a degraded identity — the same
                // handling as the GraphQL sibling (BifrostHttpMiddleware) and the chat mount.
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (outcome == BifrostIdentityOutcome.Anonymous && _requireAuthenticatedIdentity)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var bifrostRequest = await _frontend.ParseAsync(context.Request.Body, context.RequestAborted);
            if (bifrostRequest == null)
            {
                context.Response.StatusCode = 400;
                return;
            }

            // Enrich the parsed request with HTTP context. Whatever user context the
            // frontend parsed from the request wire rides SEPARATELY as WireContext:
            // the engine merges it after model resolution (WireContextMerger), because
            // which keys are identity-owned — and so can never be wire-supplied —
            // depends on model metadata (a configured tenant-context-key).
            bifrostRequest = new BifrostRequest
            {
                Query = bifrostRequest.Query,
                OperationName = bifrostRequest.OperationName,
                Variables = bifrostRequest.Variables,
                Extensions = bifrostRequest.Extensions,
                // The gate above already projected the caller through the shared factory; reusing
                // its result keeps one projection per request and makes a second, drifting
                // identity rule impossible here.
                UserContext = identityContext,
                WireContext = bifrostRequest.UserContext,
                RequestServices = context.RequestServices,
                CancellationToken = context.RequestAborted,
            };

            var result = await _engine.ExecuteAsync(bifrostRequest, _endpointPath);

            context.Response.ContentType = _frontend.ResponseContentType;
            context.Response.StatusCode = 200;
            await _frontend.SerializeAsync(context.Response.Body, result, context.RequestAborted);
        }

        private bool IsMatch(HttpRequest request)
        {
            return string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                && request.ContentType != null
                && request.ContentType.Contains(_frontend.ContentType, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Extension methods for registering protocol frontends in the ASP.NET Core pipeline.
    /// </summary>
    public static class FrontendExtensions
    {
        /// <summary>
        /// Maps a protocol frontend to a specific endpoint path.
        /// The frontend handles parsing and serialization; the BifrostEngine handles execution.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="path">The URL path to map (e.g., "/graphql").</param>
        /// <param name="frontend">The protocol frontend to handle requests at this path.</param>
        /// <param name="requireAuthentication">
        /// Overrides the auth requirement this mount inherits from the GraphQL endpoint it serves.
        /// Leave null (the default) so the two can never drift; set it only to state a posture the
        /// endpoint configuration cannot express.
        /// </param>
        /// <returns>The application builder for chaining.</returns>
        public static IApplicationBuilder UseBifrostFrontend(
            this IApplicationBuilder app,
            string path,
            IProtocolFrontend frontend,
            bool? requireAuthentication = null)
        {
            var engine = app.ApplicationServices.GetRequiredService<IBifrostEngine>();
            var requiresAuth = requireAuthentication ?? ResolveFrontendAuthRequirement(app, path);
            app.Map(path, branch =>
                branch.UseMiddleware<BifrostFrontendMiddleware>(frontend, engine, path, requiresAuth));
            return app;
        }

        /// <summary>
        /// Whether the frontend mount must require an authenticated identity, taken from the
        /// GraphQL endpoint whose schema it serves — the mount carries that endpoint's surface, so
        /// it must carry its auth requirement (AGENTS.md, HTTP-mount rule). The GraphQL endpoints
        /// enforce theirs INSIDE their own <c>Map</c> branch, which is why a frontend mount is not
        /// covered by it and has to resolve the requirement here.
        ///
        /// <para>Fail closed: a deployment configured through neither options object — or a mount
        /// whose served endpoint cannot be identified, or is ambiguous — requires authentication.
        /// Serving anonymously is only ever an EXPLICIT choice (<c>DisableAuth</c> on the endpoint,
        /// or <c>requireAuthentication: false</c> here).</para>
        /// </summary>
        private static bool ResolveFrontendAuthRequirement(IApplicationBuilder app, string path)
        {
            var multiDb = app.ApplicationServices.GetService<BifrostMultiDbOptions>();
            if (multiDb != null)
            {
                var served = multiDb.Endpoints.FirstOrDefault(
                    e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
                // A mount whose path names no registered endpoint resolves its schema by the
                // single-endpoint fallback (BifrostEngine.ExecuteAsync), so follow the same rule
                // here; with several endpoints the target is ambiguous and the safe reading is
                // "requires auth".
                served ??= multiDb.Endpoints.Count == 1 ? multiDb.Endpoints[0] : null;
                return served is null || !served.DisableAuth;
            }

            var singleDb = app.ApplicationServices.GetService<BifrostSetupOptions>();
            if (singleDb != null)
                return singleDb.IsUsingAuth;

            return true;
        }

        /// <summary>
        /// Maps the default GraphQL frontend to a specific endpoint path.
        /// Convenience method that creates a GraphQLFrontend from the registered IGraphQLSerializer.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="path">The URL path to map (e.g., "/graphql").</param>
        /// <param name="requireAuthentication">
        /// Overrides the auth requirement inherited from the served endpoint; see
        /// <see cref="UseBifrostFrontend"/>.
        /// </param>
        /// <returns>The application builder for chaining.</returns>
        public static IApplicationBuilder UseBifrostGraphQL(
            this IApplicationBuilder app,
            string path = "/graphql",
            bool? requireAuthentication = null)
        {
            var serializer = app.ApplicationServices.GetRequiredService<GraphQL.IGraphQLSerializer>();
            var frontend = new GraphQLFrontend(serializer);
            return app.UseBifrostFrontend(path, frontend, requireAuthentication);
        }

        /// <summary>
        /// Registers the BifrostEngine and its dependencies in the DI container.
        /// Call during service configuration, before UseBifrostFrontend.
        /// </summary>
        public static IServiceCollection AddBifrostEngine(this IServiceCollection services)
        {
            services.AddSingleton<IBifrostEngine>(sp =>
            {
                var documentExecuter = sp.GetRequiredService<GraphQL.IDocumentExecuter>();
                var pathCache = sp.GetRequiredService<PathCache<Inputs>>();
                var transformerService = sp.GetRequiredService<IQueryTransformerService>();
                var observers = sp.GetService<IQueryObservers>();
                return new BifrostEngine(documentExecuter, pathCache, transformerService, observers);
            });
            return services;
        }
    }
}
