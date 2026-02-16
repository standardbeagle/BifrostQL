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
    /// </summary>
    public sealed class BifrostFrontendMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IProtocolFrontend _frontend;
        private readonly IBifrostEngine _engine;
        private readonly string _endpointPath;

        public BifrostFrontendMiddleware(
            RequestDelegate next,
            IProtocolFrontend frontend,
            IBifrostEngine engine,
            string endpointPath)
        {
            _next = next;
            _frontend = frontend ?? throw new ArgumentNullException(nameof(frontend));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _endpointPath = endpointPath ?? throw new ArgumentNullException(nameof(endpointPath));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!IsMatch(context.Request))
            {
                await _next(context);
                return;
            }

            var bifrostRequest = await _frontend.ParseAsync(context.Request.Body, context.RequestAborted);
            if (bifrostRequest == null)
            {
                context.Response.StatusCode = 400;
                return;
            }

            // Enrich the parsed request with HTTP context
            bifrostRequest = new BifrostRequest
            {
                Query = bifrostRequest.Query,
                OperationName = bifrostRequest.OperationName,
                Variables = bifrostRequest.Variables,
                Extensions = bifrostRequest.Extensions,
                UserContext = BuildUserContext(context, bifrostRequest.UserContext),
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

        private static IDictionary<string, object?> BuildUserContext(HttpContext context, IDictionary<string, object?> existing)
        {
            var user = context.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                var bifrostContext = new BifrostContext(context);
                // Merge any user context from the parsed request
                foreach (var kv in existing)
                {
                    if (!bifrostContext.ContainsKey(kv.Key))
                        bifrostContext[kv.Key] = kv.Value;
                }
                return bifrostContext;
            }

            return existing.Count > 0 ? existing : new Dictionary<string, object?>();
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
        /// <returns>The application builder for chaining.</returns>
        public static IApplicationBuilder UseBifrostFrontend(
            this IApplicationBuilder app,
            string path,
            IProtocolFrontend frontend)
        {
            var engine = app.ApplicationServices.GetRequiredService<IBifrostEngine>();
            app.Map(path, branch =>
                branch.UseMiddleware<BifrostFrontendMiddleware>(frontend, engine, path));
            return app;
        }

        /// <summary>
        /// Maps the default GraphQL frontend to a specific endpoint path.
        /// Convenience method that creates a GraphQLFrontend from the registered IGraphQLSerializer.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="path">The URL path to map (e.g., "/graphql").</param>
        /// <returns>The application builder for chaining.</returns>
        public static IApplicationBuilder UseBifrostGraphQL(
            this IApplicationBuilder app,
            string path = "/graphql")
        {
            var serializer = app.ApplicationServices.GetRequiredService<GraphQL.IGraphQLSerializer>();
            var frontend = new GraphQLFrontend(serializer);
            return app.UseBifrostFrontend(path, frontend);
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
