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

            if (options.IsUsingAuth)
            {
                app.UseAuthentication().UseCookiePolicy();
                app.UseUiAuth();
            }

            foreach (var endpoint in options.Endpoints)
            {
                app.Map(endpoint.Path, branch => branch.UseMiddleware<BifrostHttpMiddleware>());
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
        /// <returns>The application builder for chaining.</returns>
        public static IApplicationBuilder UseBifrostBinary(
            this IApplicationBuilder app,
            string path = "/bifrost-ws",
            int chunkThreshold = ChunkSender.DefaultChunkThreshold,
            int ackWindow = ChunkSender.DefaultAckWindow)
        {
            var engine = app.ApplicationServices.GetRequiredService<IBifrostEngine>();
            app.Map(path, branch =>
                branch.UseMiddleware<BifrostBinaryMiddleware>(engine, path, chunkThreshold, ackWindow));
            return app;
        }
    }
}
