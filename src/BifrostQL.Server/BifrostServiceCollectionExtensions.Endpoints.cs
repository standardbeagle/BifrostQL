using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using BifrostQL.Core.Resolvers;

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
        /// <returns>The application builder for chaining.</returns>
        public static IApplicationBuilder UseBifrostBinary(
            this IApplicationBuilder app,
            string path = "/bifrost-ws",
            int chunkThreshold = ChunkSender.DefaultChunkThreshold,
            int ackWindow = ChunkSender.DefaultAckWindow,
            string[]? allowedOrigins = null,
            string? graphqlPath = null)
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
            app.Map(path, branch =>
                branch.UseMiddleware<BifrostBinaryMiddleware>(
                    engine,
                    schemaPath,
                    chunkThreshold,
                    ackWindow,
                    ChunkSender.DefaultAckTimeout,
                    origins));
            return app;
        }
    }
}
