using System.Runtime.CompilerServices;
using System.Security.Claims;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

[assembly: InternalsVisibleTo("BifrostQL.Mcp.Test")]

namespace BifrostQL.Mcp
{
    /// <summary>
    /// Selects how a <see cref="BifrostMcpAdapter"/> session establishes identity. This is the
    /// single source of truth for the adapter's auth posture: the three modes are mutually
    /// exclusive and the default is fail-closed, so a deployment that configures nothing runs
    /// with no anonymous pass-through (invariant: a dangerous opt-in defaults OFF).
    /// </summary>
    public enum McpAuthMode
    {
        /// <summary>
        /// Default. No identity source is configured, so every session runs with an EMPTY user
        /// context: schema/metadata tools (which need no tenant) work, and any tenant-filtered
        /// row read fails closed ("Tenant context required") exactly like an unauthenticated
        /// GraphQL request. Nothing is logged — this is the safe resting state.
        /// </summary>
        FailClosed = 0,

        /// <summary>
        /// Deliberate anonymous/dev opt-in. Behaves like <see cref="FailClosed"/> at runtime
        /// (empty context, tenant reads still fail closed), but enabling it is a posture change
        /// the adapter logs as a startup warning — mirroring the RESP <c>EnableWrites</c> opt-in.
        /// </summary>
        AnonymousDev,

        /// <summary>
        /// Bearer/JWT mode. The adapter validates the presented token BEFORE any identity is
        /// minted; a valid token yields a <see cref="ClaimsPrincipal"/> handed to
        /// <see cref="IBifrostAuthContextFactory"/> for projection (which throws on an unmapped
        /// issuer — fail closed). An absent or invalid token mints NO identity, so the empty
        /// context drives the same "Tenant context required" rejection — never an anonymous
        /// pass-through. The adapter parses no claims of its own.
        /// </summary>
        Bearer,
    }

    /// <summary>
    /// Configuration for the <see cref="BifrostMcpAdapter"/> auth surface. Register (optionally)
    /// alongside the adapter; when absent the adapter defaults to <see cref="McpAuthMode.FailClosed"/>.
    /// </summary>
    public sealed class McpAuthOptions
    {
        /// <summary>
        /// The auth mode. Defaults to <see cref="McpAuthMode.FailClosed"/> — the dangerous
        /// anonymous and bearer surfaces are OFF until a deployment explicitly opts in.
        /// </summary>
        public McpAuthMode Mode { get; set; } = McpAuthMode.FailClosed;

        /// <summary>
        /// The bearer token presented for the session (sourced by the host from its environment/
        /// configuration; a stdio session has one caller). Used only in <see cref="McpAuthMode.Bearer"/>.
        /// Null or empty presents no credential, so identity fails closed.
        /// </summary>
        public string? BearerToken { get; set; }

        /// <summary>
        /// Validates a presented bearer token, returning the authenticated
        /// <see cref="ClaimsPrincipal"/> for a valid token or <c>null</c> for an invalid one.
        /// The host supplies its JWT handler here; the adapter never reads claims itself — it
        /// hands the whole principal to <see cref="IBifrostAuthContextFactory"/> for projection.
        /// Null validator (or null token) yields no identity.
        /// </summary>
        public Func<string, ClaimsPrincipal?>? ValidateBearerToken { get; set; }
    }

    /// <summary>
    /// Hosts a BifrostQL MCP server over stdio as an <see cref="IProtocolAdapter"/>.
    /// Register with <c>AddBifrostQL(o =&gt; o.AddProtocolAdapter&lt;BifrostMcpAdapter&gt;())</c>;
    /// the host process's stdin/stdout then speak MCP (JSON-RPC), so nothing else
    /// in the process may write to stdout.
    ///
    /// <para><b>Identity (stdio dev mode).</b> A stdio MCP session carries no
    /// per-request principal: the caller is whoever launched the process, so the
    /// session's effective identity is scoped to the endpoint's connection string
    /// and every request executes with an <b>empty user context</b> — there is no
    /// tenant id, user id, or role projection. That empty context is produced by
    /// the SAME shared <see cref="IBifrostAuthContextFactory"/> every other
    /// transport gate uses (fail-closed), never by bespoke claim parsing here:
    /// a stdio session has no authenticated principal, so the factory projects an
    /// empty context. The schema tools this adapter exposes only read the cached
    /// <see cref="Core.Model.IDbModel"/> (metadata, never rows), which requires no
    /// tenant. Row-reading tools execute through <see cref="IQueryIntentExecutor"/>,
    /// where a tenant-filtered table combined with this empty context fails closed
    /// ("Tenant context required") exactly like an unauthenticated GraphQL request
    /// — never an anonymous pass-through. The provider is invoked per tool call
    /// (not captured once), leaving the re-resolution seam a later slice needs to
    /// attach a per-session principal to the carrier.</para>
    ///
    /// <para>The MCP surface itself is built by
    /// <see cref="BifrostMcpServerFactory.CreateServerOptions"/>; this class owns
    /// only the stdio transport lifecycle.</para>
    /// </summary>
    public sealed class BifrostMcpAdapter : IProtocolAdapter
    {
        private readonly IQueryIntentExecutor _executor;
        private readonly IBifrostAuthContextFactory _authContextFactory;
        private readonly IServiceProvider _services;
        private readonly McpAuthOptions _authOptions;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<BifrostMcpAdapter> _logger;
        private readonly CancellationTokenSource _stopping = new();
        private McpServer? _server;
        private Task? _runTask;

        public BifrostMcpAdapter(
            IQueryIntentExecutor executor,
            IBifrostAuthContextFactory authContextFactory,
            IServiceProvider services,
            ILoggerFactory? loggerFactory = null,
            McpAuthOptions? authOptions = null)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _authContextFactory = authContextFactory ?? throw new ArgumentNullException(nameof(authContextFactory));
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _authOptions = authOptions ?? new McpAuthOptions();
            _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            _logger = _loggerFactory.CreateLogger<BifrostMcpAdapter>();
        }

        /// <summary>
        /// Builds the per-call user-context provider from the shared
        /// <see cref="IBifrostAuthContextFactory"/> under the default fail-closed auth mode.
        /// Identity is sourced ONLY through the factory — this adapter parses no claims of its
        /// own. A stdio session carries no authenticated principal, so the factory projects an
        /// empty (fail-closed) context; the provider is re-invoked on every tool call so a later
        /// slice can attach a per-session principal to the carrier and have identity re-resolved
        /// each call.
        /// </summary>
        internal static Func<IDictionary<string, object?>> CreateUserContextProvider(
            IBifrostAuthContextFactory authContextFactory, IServiceProvider services)
            => CreateUserContextProvider(authContextFactory, services, new McpAuthOptions());

        /// <summary>
        /// Builds the per-call user-context provider selected by <paramref name="authOptions"/>.
        /// Identity flows ONLY through <paramref name="authContextFactory"/>: in bearer mode the
        /// presented token is validated FIRST and, only if valid, its
        /// <see cref="ClaimsPrincipal"/> is attached to the carrier for the factory to project;
        /// anonymous/dev and fail-closed modes attach no principal, so the factory projects an
        /// empty context and tenant-filtered reads fail closed. The adapter reads no claims and
        /// builds no predicate — an unmapped issuer or absent/invalid token yields no identity,
        /// never an anonymous pass-through.
        /// </summary>
        internal static Func<IDictionary<string, object?>> CreateUserContextProvider(
            IBifrostAuthContextFactory authContextFactory, IServiceProvider services, McpAuthOptions authOptions)
            => () =>
            {
                var carrier = new DefaultHttpContext { RequestServices = services };

                // Bearer mode: validate the presented token BEFORE any identity is minted. Only a
                // valid token attaches a principal; an absent/invalid token leaves the carrier
                // unauthenticated so the factory projects an empty (fail-closed) context.
                if (authOptions.Mode == McpAuthMode.Bearer)
                {
                    var principal = ValidateBearer(authOptions);
                    if (principal is not null)
                        carrier.User = principal;
                }

                return authContextFactory.CreateUserContext(carrier);
            };

        /// <summary>
        /// Validates the presented bearer token via the host-supplied validator, returning its
        /// principal or <c>null</c>. No token or no validator means no credential — no identity.
        /// The adapter itself never inspects the token or its claims.
        /// </summary>
        private static ClaimsPrincipal? ValidateBearer(McpAuthOptions authOptions)
        {
            var token = authOptions.BearerToken;
            if (string.IsNullOrEmpty(token) || authOptions.ValidateBearerToken is null)
                return null;
            return authOptions.ValidateBearerToken(token);
        }

        /// <summary>
        /// Applies the configured auth mode at startup: logs the deliberate-opt-in warning when
        /// anonymous/dev mode is enabled (mirroring the RESP <c>EnableWrites</c> posture warning)
        /// and returns the user-context provider the mode selects. Extracted from
        /// <see cref="StartAsync"/> so the startup posture is observable without engaging the
        /// stdio transport.
        /// </summary>
        internal Func<IDictionary<string, object?>> ConfigureAuth()
        {
            LogStartupAuthPosture(_logger, _authOptions);
            return CreateUserContextProvider(_authContextFactory, _services, _authOptions);
        }

        /// <summary>
        /// Emits the startup auth-posture log lines: a <see cref="LogLevel.Warning"/> naming the
        /// anonymous/dev opt-in (same shape as <c>RespWireAdapter</c>'s <c>EnableWrites</c>
        /// warning), plus an informational line stating the effective mode.
        /// </summary>
        internal static void LogStartupAuthPosture(ILogger logger, McpAuthOptions authOptions)
        {
            if (authOptions.Mode == McpAuthMode.AnonymousDev)
                logger.LogWarning(
                    "MCP front door started with ANONYMOUS/DEV auth mode ENABLED — sessions run without " +
                    "an established identity (empty user context). This is a deliberate opt-in; leave the " +
                    "default fail-closed auth mode unless anonymous access is intended.");

            logger.LogInformation("MCP front door ready (auth mode: {Mode}).", authOptions.Mode);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_server is not null)
                throw new InvalidOperationException("BifrostMcpAdapter is already started.");

            // Honor host-initiated cancellation before committing to the stdio
            // session; once RunAsync starts, only StopAsync (via _stopping) ends it.
            cancellationToken.ThrowIfCancellationRequested();

            var options = BifrostMcpServerFactory.CreateServerOptions(
                _executor,
                userContextProvider: ConfigureAuth());
            var transport = new StdioServerTransport(BifrostMcpServerFactory.ServerName, _loggerFactory);
            _server = McpServer.Create(transport, options, _loggerFactory, serviceProvider: null);

            // The session outlives StartAsync: it runs until the client closes
            // stdin or the host stops. Faults surface on StopAsync (graceful
            // shutdown always awaits the session) rather than being swallowed.
            _runTask = _server.RunAsync(_stopping.Token);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_server is null || _runTask is null)
                return;

            await _stopping.CancelAsync();
            try
            {
                await _runTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the normal stdio shutdown path.
            }
            finally
            {
                await _server.DisposeAsync();
                _server = null;
                _runTask = null;
                _stopping.Dispose();
            }
        }
    }
}
