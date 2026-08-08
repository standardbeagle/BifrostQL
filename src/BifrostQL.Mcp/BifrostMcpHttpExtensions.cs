using System.Security.Cryptography;
using System.Text;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using Microsoft.AspNetCore.Http;
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
    /// <para><b>Identity per REQUEST.</b> Identity is derived exactly as every other
    /// Bifrost transport gate does — never with bespoke claim mapping. The bearer is
    /// extracted from the <c>Authorization</c> header via the
    /// <see cref="McpCredentialSources"/> seam, the async credential exchange is
    /// <b>awaited</b> (so no sync-over-async bridge runs on the ASP.NET request path),
    /// and the resolved principal is projected through the shared
    /// <see cref="IBifrostAuthContextFactory"/>. A token from an unmapped OIDC issuer
    /// surfaces as a sanitized MCP error and never degrades to an empty/anonymous
    /// context; an absent/invalid token mints no identity, so tenant-filtered reads fail
    /// closed exactly like the stdio path.</para>
    ///
    /// <para>This runs on EVERY request, not once at <c>initialize</c>, and the session
    /// is bound to the credential it was opened with — see
    /// <see cref="McpHttpSessionIdentity"/> for what that closes.</para>
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

            // Required by the per-request identity guard: the MCP HTTP transport flows the
            // originating request's ExecutionContext with each JSON-RPC message, so the
            // accessor resolves the request the handler is actually serving — not the one
            // that happened to open the session.
            services.AddHttpContextAccessor();

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

#pragma warning disable CS1998 // async signature is fixed by ConfigureSessionOptions
        private static async Task ConfigureSessionAsync(
            HttpContext httpContext,
            ModelContextProtocol.Server.McpServerOptions sessionOptions,
            McpAuthOptions authOptions,
            string? endpoint,
            CancellationToken cancellationToken)
        {
            var requestServices = httpContext.RequestServices;
            var executor = requestServices.GetRequiredService<IQueryIntentExecutor>();
            var mutationExecutor = requestServices.GetService<IMutationIntentExecutor>();

            // The session is BOUND to the credential presented at initialize; identity
            // itself is re-derived on every request by the guard below, so an expired or
            // revoked token stops working at the next request instead of at disconnect.
            var authorizationHeader = httpContext.Request.Headers.Authorization.ToString();
            var sessionCredential = authOptions.Mode == McpAuthMode.Bearer
                ? McpCredentialSources.ExtractBearerToken(authorizationHeader)
                : null;
            var sessionIdentity = new McpHttpSessionIdentity(
                authOptions,
                requestServices.GetRequiredService<IHttpContextAccessor>(),
                sessionCredential);

            // Deliberately NOT revalidated here. Opening a session is not access: every
            // handler runs the guard first, so a session opened on a credential that does
            // not resolve can perform nothing, and the reason surfaces as a sanitized tool
            // error rather than as an unhandled fault on the initialize request.
            var provider = sessionIdentity.Current;
            var logger = requestServices.GetRequiredService<ILoggerFactory>().CreateLogger<BifrostMcpAdapter>();
            // Opt-in declarative tools, resolved from the configured document (null when
            // AddBifrostMcpTools was never called — the built-in surface only).
            var declarativeTools = requestServices.GetService<DeclarativeToolDocument>();
            var sessionScoped = BifrostMcpServerFactory.CreateServerOptions(
                executor,
                endpoint,
                userContextProvider: provider,
                mutationExecutor: mutationExecutor,
                enableWrites: authOptions.EnableWrites,
                toolPolicy: authOptions.ToolPolicy,
                logger: logger,
                declarativeTools: declarativeTools,
                beforeRequestAsync: sessionIdentity.RevalidateAsync);

            sessionOptions.ServerInfo = sessionScoped.ServerInfo;
            sessionOptions.ServerInstructions = sessionScoped.ServerInstructions;
            sessionOptions.Capabilities = sessionScoped.Capabilities;
            sessionOptions.Handlers = sessionScoped.Handlers;
        }
#pragma warning restore CS1998

        /// <summary>
        /// Per-request identity for one HTTP MCP session.
        ///
        /// <para>Identity used to be resolved ONCE, at the session's <c>initialize</c>
        /// request, and frozen for the session's lifetime. Two consequences, both
        /// fail-open: a token that expired or was revoked kept full access until the
        /// client disconnected, and every request after <c>initialize</c> needed only the
        /// session id — no <c>Authorization</c> header at all — because nothing looked at
        /// the header again.</para>
        ///
        /// <para>Now every request re-derives identity before the handler runs. The
        /// session is BOUND to the credential presented at initialize: the credential on
        /// each subsequent request must still be that one (compared in fixed time, run
        /// unconditionally per invariant 2), and it must STILL resolve to a principal —
        /// so a revoked/expired token ends the session's access at the next request. The
        /// principal is then projected through the shared
        /// <see cref="IBifrostAuthContextFactory"/> using the CURRENT request's service
        /// scope, which also removes the reason the old code had to snapshot: it no
        /// longer reads a scope that the initiating request already disposed.</para>
        ///
        /// <para>Every failure throws <see cref="McpIdentityException"/> — in the server
        /// factory's funnelled condition set (invariant 1), carrying a constant sanitized
        /// message. It never degrades to an empty/anonymous context.</para>
        /// </summary>
        internal sealed class McpHttpSessionIdentity
        {
            private const string ContextItemKey = "bifrost.mcp.user-context";

            private readonly McpAuthOptions _authOptions;
            private readonly IHttpContextAccessor _accessor;
            private readonly string? _sessionCredential;

            internal McpHttpSessionIdentity(
                McpAuthOptions authOptions, IHttpContextAccessor accessor, string? sessionCredential)
            {
                _authOptions = authOptions;
                _accessor = accessor;
                _sessionCredential = sessionCredential;
            }

            /// <summary>
            /// Re-validates the caller and stashes the freshly projected user context on the
            /// CURRENT request. Runs before every handler; throws (fail closed) rather than
            /// yielding a weaker identity.
            /// </summary>
            internal async ValueTask RevalidateAsync(CancellationToken cancellationToken)
            {
                var httpContext = _accessor.HttpContext
                    ?? throw new McpIdentityException();

                var header = httpContext.Request.Headers.Authorization.ToString();
                var presented = _authOptions.Mode == McpAuthMode.Bearer
                    ? McpCredentialSources.ExtractBearerToken(header)
                    : null;

                if (!CredentialMatchesSession(presented))
                    throw new McpIdentityException();

                var principal = await BifrostMcpAdapter
                    .ResolveBearerPrincipalAsync(_authOptions, header, cancellationToken)
                    .ConfigureAwait(false);

                // A session established WITH a credential must keep resolving one. This is
                // the expiry/revocation gate: nothing else re-checks the token's validity.
                if (_sessionCredential is not null && principal is null)
                    throw new McpIdentityException();

                var factory = BifrostAuthContextFactory.Resolve(httpContext);
                var carrier = new DefaultHttpContext
                {
                    RequestServices = httpContext.RequestServices,
                };
                if (principal is not null)
                    carrier.User = principal;

                // An unmapped OIDC issuer throws UnmappedOidcIssuerException here; it is in
                // the same funnelled set and is sanitized onto the wire, never swallowed.
                httpContext.Items[ContextItemKey] = factory.CreateUserContext(carrier);
            }

            /// <summary>
            /// The context <see cref="RevalidateAsync"/> established for THIS request. A
            /// missing entry means the guard did not run for this request, which is a
            /// fail-closed condition, never a reason to fall back to an empty context.
            /// </summary>
            internal IDictionary<string, object?> Current()
                => _accessor.HttpContext?.Items.TryGetValue(ContextItemKey, out var value) == true
                    && value is IDictionary<string, object?> context
                    ? new Dictionary<string, object?>(context)
                    : throw new McpIdentityException();

            /// <summary>
            /// Fixed-time comparison of the presented credential against the session's. The
            /// compare runs UNCONDITIONALLY and the presence check is ANDed afterwards
            /// (invariant 2) — gating the compare behind a null check would leak, by timing,
            /// whether the session carries a credential at all.
            /// </summary>
            private bool CredentialMatchesSession(string? presented)
            {
                var session = Encoding.UTF8.GetBytes(_sessionCredential ?? string.Empty);
                var candidate = Encoding.UTF8.GetBytes(presented ?? string.Empty);
                var equal = session.Length == candidate.Length
                    && CryptographicOperations.FixedTimeEquals(session, candidate);
                return equal && (_sessionCredential is null) == (presented is null);
            }
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
