using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.S3;

namespace BifrostQL.Server
{
    public static partial class BifrostServiceCollectionExtensions
    {
        /// <summary>
        /// Registers multiple BifrostQL database endpoints. Each endpoint serves a different
        /// database at its own GraphQL path with independent configuration.
        /// </summary>
        public static IServiceCollection AddBifrostEndpoints(this IServiceCollection services, Action<BifrostMultiDbOptions> optionSetter)
        {
            var options = new BifrostMultiDbOptions();
            optionSetter(options);
            options.ConfigureServices(services);
            return services;
        }

        /// <summary>
        /// Maps all registered BifrostQL database endpoints to their configured paths.
        /// Call after AddBifrostEndpoints in the service configuration.
        /// </summary>
        public static IApplicationBuilder UseBifrostEndpoints(this IApplicationBuilder app)
        {
            var options = app.ApplicationServices.GetService<BifrostMultiDbOptions>();
            if (options == null) throw new InvalidOperationException("BifrostMultiDbOptions not configured. Call AddBifrostEndpoints before UseBifrostEndpoints");

            // Authentication middleware runs globally only to POPULATE HttpContext.User; it
            // does NOT gate anything by itself. Enforcement is endpoint-scoped below so an
            // endpoint that requires auth challenges/denies regardless of sibling endpoints or
            // pipeline ordering — the previous single aggregate toggle let a DisableAuth=false
            // endpoint serve anonymous traffic depending on order.
            if (options.IsUsingAuth)
                app.UseAuthentication().UseCookiePolicy();

            foreach (var endpoint in options.Endpoints)
            {
                var requiresAuth = !endpoint.DisableAuth;
                app.Map(endpoint.Path, branch =>
                {
                    // Per-endpoint fail-closed gate: an unauthenticated request to an
                    // auth-required endpoint is challenged (and OIDC principals normalized)
                    // inside this branch before the GraphQL middleware ever runs.
                    if (requiresAuth)
                        branch.UseUiAuth();
                    branch.UseMiddleware<BifrostHttpMiddleware>();
                });
                app.UseGraphQLGraphiQL(endpoint.PlaygroundPath,
                    new GraphQL.Server.Ui.GraphiQL.GraphiQLOptions
                    {
                        GraphQLEndPoint = endpoint.Path,
                        SubscriptionsEndPoint = endpoint.Path,
                        RequestCredentials = GraphQL.Server.Ui.GraphiQL.RequestCredentials.SameOrigin,
                    });
            }
            return app;
        }

        /// <summary>
        /// Maps the BifrostQL binary WebSocket endpoint at the specified path.
        /// Clients connect via WebSocket and exchange protobuf-encoded binary frames.
        /// Large responses are automatically chunked with CRC32 integrity checksums
        /// and backpressure via ACK windowing.
        /// Requires AddBifrostEngine() in service configuration and UseWebSockets() before this call.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="path">The WebSocket endpoint path (e.g., "/bifrost-ws").</param>
        /// <param name="chunkThreshold">Payload size threshold for chunking (default 64 KB).</param>
        /// <param name="ackWindow">Maximum unacknowledged chunks before backpressure pauses sending (default 8).</param>
        /// <param name="allowedOrigins">
        /// Origins permitted to open a cross-origin WebSocket handshake. Null or empty means
        /// same-origin only (a WebSocket handshake bypasses CORS, so this is the CSWSH guard).
        /// </param>
        /// <param name="graphqlPath">
        /// The registered GraphQL endpoint path whose schema the binary transport serves. When
        /// null, the single registered GraphQL endpoint is used. Set this when more than one
        /// GraphQL endpoint is registered so the binary transport resolves the intended schema.
        /// </param>
        /// <param name="requireAuthentication">
        /// Overrides the auth requirement this mount inherits from the GraphQL endpoint it
        /// serves. Leave null (the default) so the two can never drift; set it only to state a
        /// posture the endpoint configuration cannot express.
        /// </param>
        /// <param name="maxConnections">
        /// Concurrent-connection cap for this mount. The slot is taken at the upgrade, before
        /// the identity gate, since an unauthenticated peer's connection already costs a receive
        /// buffer and a reassembly budget.
        /// </param>
        /// <param name="firstFrameTimeout">
        /// Deadline for an admitted connection's first frame (default 30 s) — the pre-auth
        /// deadline that stops a silent peer from holding an admission slot for free.
        /// </param>
        /// <param name="idleTimeout">Deadline between frames of an established connection (default 10 min).</param>
        /// <returns>The application builder for chaining.</returns>
        public static IApplicationBuilder UseBifrostBinary(
            this IApplicationBuilder app,
            string path = "/bifrost-ws",
            int chunkThreshold = ChunkSender.DefaultChunkThreshold,
            int ackWindow = ChunkSender.DefaultAckWindow,
            string[]? allowedOrigins = null,
            string? graphqlPath = null,
            bool? requireAuthentication = null,
            int maxConnections = BifrostBinaryMiddleware.DefaultMaxConnections,
            TimeSpan? firstFrameTimeout = null,
            TimeSpan? idleTimeout = null)
        {
            var engine = app.ApplicationServices.GetRequiredService<IBifrostEngine>();
            // The schema-resolution key is the GraphQL endpoint path, not the WebSocket mount
            // path (BifrostEngine keys the PathCache by GraphQL path). Default to the mount
            // path, which BifrostEngine falls back from to the single registered GraphQL
            // endpoint when it is not itself a registered path.
            var schemaPath = graphqlPath ?? path;
            // Pass a concrete (never-null) list so UseMiddleware can match the argument by
            // type; an empty list is equivalent to "same-origin only" in the middleware.
            IReadOnlyList<string> origins = allowedOrigins ?? Array.Empty<string>();
            var requiresAuth = requireAuthentication ?? ResolveBinaryAuthRequirement(app, schemaPath);
            app.Map(path, branch =>
                branch.UseMiddleware<BifrostBinaryMiddleware>(
                    engine,
                    schemaPath,
                    chunkThreshold,
                    ackWindow,
                    ChunkSender.DefaultAckTimeout,
                    requiresAuth,
                    origins,
                    maxConnections,
                    // UseMiddleware matches constructor arguments by their runtime type, so a
                    // null Nullable<TimeSpan> would match nothing: unwrap to the middleware's
                    // "unset" sentinel here.
                    firstFrameTimeout ?? default,
                    idleTimeout ?? default));
            return app;
        }

        /// <summary>
        /// Whether the binary mount must require an authenticated identity, taken from the
        /// GraphQL endpoint whose schema it serves — the transport carries that endpoint's
        /// surface, so it must carry its auth requirement. The GraphQL endpoints enforce theirs
        /// INSIDE their own <c>Map</c> branch, which is why a binary mount at its own path is
        /// not covered by it and has to resolve the requirement here.
        ///
        /// <para>Fail closed: a deployment that has not been configured through either options
        /// object — or one whose binary mount serves an endpoint that cannot be identified —
        /// requires authentication. Serving anonymously is only ever an EXPLICIT choice
        /// (<c>DisableAuth</c> on the endpoint, or <c>requireAuthentication: false</c> here).</para>
        /// </summary>
        private static bool ResolveBinaryAuthRequirement(IApplicationBuilder app, string schemaPath)
        {
            var multiDb = app.ApplicationServices.GetService<BifrostMultiDbOptions>();
            if (multiDb != null)
            {
                var served = multiDb.Endpoints.FirstOrDefault(
                    e => string.Equals(e.Path, schemaPath, StringComparison.OrdinalIgnoreCase));
                // A mount whose graphqlPath names no registered endpoint resolves its schema by
                // the single-endpoint fallback, so follow the same rule here; with several
                // endpoints the target is ambiguous and the safe reading is "requires auth".
                served ??= multiDb.Endpoints.Count == 1 ? multiDb.Endpoints[0] : null;
                return served is null || !served.DisableAuth;
            }

            var singleDb = app.ApplicationServices.GetService<BifrostSetupOptions>();
            if (singleDb != null)
                return singleDb.IsUsingAuth;

            return true;
        }

        /// <summary>
        /// Mounts the opt-in S3-compatible HTTP endpoint when it has been enabled via
        /// <see cref="BifrostSetupOptions.AddS3Endpoint"/> /
        /// <see cref="BifrostMultiDbOptions.AddS3Endpoint"/>. A no-op when the endpoint was not
        /// enabled, so a host can call it unconditionally.
        ///
        /// <para>Fail-fast: an enabled endpoint requires a host-supplied
        /// <see cref="IS3AccessKeyStore"/> (there is no fallback identity source) — its absence
        /// throws here at pipeline build, never at first request. Enabling the endpoint logs a
        /// startup warning, since exposing an S3 front door is a posture change worth
        /// surfacing.</para>
        /// </summary>
        public static IApplicationBuilder UseBifrostS3(this IApplicationBuilder app)
        {
            var options = app.ApplicationServices.GetService<S3Options>();
            if (options is null || !options.Enabled)
                return app;

            if (app.ApplicationServices.GetService<IS3AccessKeyStore>() is null)
                throw new InvalidOperationException(
                    "The S3 endpoint is enabled but no IS3AccessKeyStore is registered. " +
                    "Register one (there is no fallback identity source) before UseBifrostS3.");

            app.ApplicationServices.GetService<ILoggerFactory>()
                ?.CreateLogger("BifrostQL.Server.S3")
                .LogWarning("S3-compatible HTTP endpoint is ENABLED at prefix '{Prefix}' (region '{Region}'). " +
                    "This exposes an SigV4-authenticated front door; ensure the access-key store is trusted.",
                    options.PathPrefix, options.Region);

            app.Map(options.PathPrefix, branch =>
                branch.UseMiddleware<S3Middleware>(options));
            return app;
        }
    }
}
