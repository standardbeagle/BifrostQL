using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace BifrostQL.Mcp
{
    /// <summary>
    /// Hosts the BifrostQL MCP server over the Streamable HTTP transport (the MCP
    /// library's built-in HTTP hosting: <c>WithHttpTransport</c> + <c>MapMcp</c>),
    /// the HTTP sibling of the stdio <see cref="BifrostMcpAdapter"/>.
    ///
    /// <para><b>Identity per session.</b> Identity is derived exactly as every other
    /// Bifrost transport gate does — never with bespoke claim mapping. On the request
    /// that initiates a session the bearer is extracted from the <c>Authorization</c>
    /// header via slice C's <see cref="McpCredentialSources"/> seam, the async
    /// credential exchange is <b>awaited</b> in <c>ConfigureSessionOptions</c> (so no
    /// sync-over-async bridge runs on the ASP.NET request path), and the resolved
    /// principal is projected through the shared <see cref="IBifrostAuthContextFactory"/>.
    /// A token from an unmapped OIDC issuer surfaces as a sanitized MCP error and never
    /// degrades to an empty/anonymous context; an absent/invalid token mints no identity,
    /// so tenant-filtered reads fail closed exactly like the stdio path.</para>
    /// </summary>
    public static class BifrostMcpHttpExtensions
    {
        /// <summary>
        /// Registers the BifrostQL MCP server on the Streamable HTTP transport.
        /// <paramref name="authOptions"/> selects the auth posture (default
        /// <see cref="McpAuthMode.FailClosed"/>) and the write opt-in;
        /// <paramref name="endpoint"/> selects the registered GraphQL endpoint whose
        /// cached model/connection the tools execute against (null = the single
        /// registered endpoint). Call <see cref="MapBifrostMcp"/> to expose the route.
        /// </summary>
        public static IMcpServerBuilder AddBifrostMcpHttp(
            this IServiceCollection services,
            McpAuthOptions? authOptions = null,
            string? endpoint = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            var options = authOptions ?? new McpAuthOptions();

            // Surface the startup posture once (writes/anonymous opt-ins are posture
            // changes worth logging), mirroring the stdio adapter's startup warning.
            services.AddHostedService(sp => new McpHttpPostureLogger(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<BifrostMcpAdapter>(), options));

            return services
                .AddMcpServer(server =>
                {
                    // Static session-independent metadata; the per-session handlers
                    // (which carry the caller's identity + endpoint) are bound in
                    // ConfigureSessionOptions where request DI is available.
                    server.ServerInfo = new Implementation
                    {
                        Name = "BifrostQL",
                        Version = typeof(BifrostMcpHttpExtensions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                    };
                    server.Capabilities = new ServerCapabilities
                    {
                        Tools = new ToolsCapability(),
                        Resources = new ResourcesCapability(),
                    };
                })
                .WithHttpTransport(http =>
                {
                    // Invoked once per session at the initialize request (stateful mode),
                    // with that request's HttpContext — the seam for per-session identity.
                    http.ConfigureSessionOptions = (httpContext, sessionOptions, ct) =>
                        ConfigureSessionAsync(httpContext, sessionOptions, options, endpoint, ct);
                });
        }

        /// <summary>Maps the Streamable HTTP MCP endpoint (default <c>/mcp</c>).</summary>
        public static IEndpointConventionBuilder MapBifrostMcp(this IEndpointRouteBuilder endpoints, string pattern = "/mcp")
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            return endpoints.MapMcp(pattern);
        }

        private static async Task ConfigureSessionAsync(
            Microsoft.AspNetCore.Http.HttpContext httpContext,
            ModelContextProtocol.Server.McpServerOptions sessionOptions,
            McpAuthOptions authOptions,
            string? endpoint,
            CancellationToken cancellationToken)
        {
            var requestServices = httpContext.RequestServices;
            var executor = requestServices.GetRequiredService<IQueryIntentExecutor>();
            var mutationExecutor = requestServices.GetService<IMutationIntentExecutor>();
            var factory = BifrostAuthContextFactory.Resolve(httpContext);

            // Extract the bearer from THIS session's initiating request and run the
            // async credential exchange here — awaited, never bridged.
            var authorizationHeader = httpContext.Request.Headers.Authorization.ToString();
            var principal = await BifrostMcpAdapter.ResolveBearerPrincipalAsync(
                authOptions, authorizationHeader, cancellationToken).ConfigureAwait(false);

            var provider = BifrostMcpAdapter.CreateProjectionProvider(factory, requestServices, principal);
            var logger = requestServices.GetRequiredService<ILoggerFactory>().CreateLogger<BifrostMcpAdapter>();
            var sessionScoped = BifrostMcpServerFactory.CreateServerOptions(
                executor,
                endpoint,
                userContextProvider: provider,
                mutationExecutor: mutationExecutor,
                enableWrites: authOptions.EnableWrites,
                toolPolicy: authOptions.ToolPolicy,
                logger: logger);

            sessionOptions.ServerInfo = sessionScoped.ServerInfo;
            sessionOptions.ServerInstructions = sessionScoped.ServerInstructions;
            sessionOptions.Capabilities = sessionScoped.Capabilities;
            sessionOptions.Handlers = sessionScoped.Handlers;
        }

        /// <summary>Logs the MCP HTTP front-door posture once at host startup.</summary>
        private sealed class McpHttpPostureLogger : IHostedService
        {
            private readonly ILogger _logger;
            private readonly McpAuthOptions _options;

            public McpHttpPostureLogger(ILogger logger, McpAuthOptions options)
            {
                _logger = logger;
                _options = options;
            }

            public Task StartAsync(CancellationToken cancellationToken)
            {
                BifrostMcpAdapter.LogStartupAuthPosture(_logger, _options);
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
