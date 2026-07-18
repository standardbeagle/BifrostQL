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
        /// Whether the MCP write tools (<c>bifrost_insert</c>, <c>bifrost_update</c>,
        /// <c>bifrost_delete</c>) are exposed. Defaults to <c>false</c> — the dangerous
        /// write surface is OFF until a deployment explicitly opts in, mirroring the RESP
        /// <c>EnableWrites</c> posture. When disabled the write tools are never listed, so
        /// a disabled surface builds zero intent and cannot be probed; enabling it is a
        /// posture change the adapter logs as a startup warning. Writes route exclusively
        /// through <see cref="IMutationIntentExecutor"/> (the full mutation pipeline).
        /// </summary>
        public bool EnableWrites { get; set; }

        /// <summary>
        /// The bearer token presented for the session (sourced by the host from its environment/
        /// configuration; a stdio session has one caller). Used only in <see cref="McpAuthMode.Bearer"/>
        /// and only when <see cref="CredentialSource"/> is not set. Null or empty presents no
        /// credential, so identity fails closed.
        /// </summary>
        public string? BearerToken { get; set; }

        /// <summary>
        /// Per-transport credential-extraction seam. When set, this delegate — not
        /// <see cref="BearerToken"/> — supplies the raw credential for the session/request in
        /// <see cref="McpAuthMode.Bearer"/>. It knows only WHERE the credential is read (a stdio
        /// process env var / initialize-handshake value, or an HTTP <c>Authorization: Bearer</c>
        /// header), never HOW identity is projected: the extracted credential still flows through
        /// the identical <see cref="ValidateBearerToken"/> + <see cref="IBifrostAuthContextFactory"/>
        /// projection, so swapping stdio for HTTP changes only the read step. Build one with
        /// <see cref="McpCredentialSources"/>. A source that returns null (unset env var, missing
        /// header) presents no credential, so identity fails closed — never anonymous.
        /// </summary>
        public Func<string?>? CredentialSource { get; set; }

        /// <summary>
        /// Validates a presented bearer token, returning the authenticated
        /// <see cref="ClaimsPrincipal"/> for a valid token or <c>null</c> for an invalid one.
        /// The host supplies its JWT handler here; the adapter never reads claims itself — it
        /// hands the whole principal to <see cref="IBifrostAuthContextFactory"/> for projection.
        /// Null validator (or null token) yields no identity.
        /// </summary>
        public Func<string, ClaimsPrincipal?>? ValidateBearerToken { get; set; }

        /// <summary>
        /// Optional OIDC / token-exchange credential store. When set, the extracted credential
        /// (the upstream IdP token supplied by <see cref="CredentialSource"/>) is handed to the
        /// store's <see cref="IMcpCredentialStore.ExchangeAsync"/> instead of
        /// <see cref="ValidateBearerToken"/> — the store performs a token exchange and returns the
        /// candidate <see cref="ClaimsPrincipal"/>, which still flows through the identical
        /// <see cref="IBifrostAuthContextFactory"/> projection. Like
        /// <see cref="BifrostQL.Server.Pgwire.IPgCredentialStore"/>
        /// it is OFF unless a deployment configures it: absent a store, the MCP front door attempts
        /// NO exchange and falls back to slice-A/B fail-closed behavior. A store that returns
        /// <c>null</c> (unknown/failed exchange) mints no identity — never an ambient/anonymous one.
        /// </summary>
        public IMcpCredentialStore? CredentialStore { get; set; }

        /// <summary>
        /// Governs the exposed tool surface: the declared-tool-count budget guardrail and the optional
        /// per-identity role→tool allow-list. Defaults to the standard budget with no role gating (the
        /// full surface is visible). Applied by <see cref="BifrostMcpServerFactory.CreateServerOptions"/>.
        /// </summary>
        public McpToolPolicyOptions ToolPolicy { get; set; } = new();
    }

    /// <summary>
    /// Resolves an upstream IdP (OIDC) token into a candidate Bifrost identity by token exchange.
    /// This is the MCP analogue of <see cref="BifrostQL.Server.Pgwire.IPgCredentialStore"/> and shares its single hard
    /// rule: a failed/unknown exchange resolves to <c>null</c> (identity fails closed), NEVER to an
    /// ambient/anonymous principal — the returned principal is the <i>candidate</i> only, still
    /// projected through <see cref="IBifrostAuthContextFactory"/>, which is where a subject-less or
    /// unmapped-issuer principal is rejected. There is deliberately no default registration: a
    /// deployment that wants token exchange must supply one, so the front door can never come up
    /// exchanging everyone to nobody.
    /// </summary>
    public interface IMcpCredentialStore
    {
        /// <summary>
        /// Exchanges <paramref name="upstreamToken"/> (the credential extracted by
        /// <see cref="McpAuthOptions.CredentialSource"/>) for a candidate <see cref="ClaimsPrincipal"/>,
        /// or returns <c>null</c> when the token is unknown or the exchange fails. Never returns a
        /// fallback/ambient identity.
        /// </summary>
        Task<ClaimsPrincipal?> ExchangeAsync(string upstreamToken, CancellationToken cancellationToken);
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
        private readonly IMutationIntentExecutor? _mutationExecutor;
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
            McpAuthOptions? authOptions = null,
            IMutationIntentExecutor? mutationExecutor = null)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _mutationExecutor = mutationExecutor;
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
        /// Resolves the presented credential into a candidate <see cref="ClaimsPrincipal"/>, or
        /// <c>null</c> when none can be established. The token is read through the per-transport
        /// <see cref="McpAuthOptions.CredentialSource"/> when set (falling back to the static
        /// <see cref="McpAuthOptions.BearerToken"/>). When an
        /// <see cref="McpAuthOptions.CredentialStore"/> is configured (the OIDC / token-exchange
        /// opt-in) the extracted credential is an upstream IdP token exchanged by the store;
        /// otherwise the host-supplied <see cref="McpAuthOptions.ValidateBearerToken"/> validates it.
        /// No token means no credential (the store/validator is never consulted); an unknown/failed
        /// exchange or an invalid token means no identity. The adapter itself never inspects the
        /// token or its claims — it hands the whole candidate principal to the factory.
        /// </summary>
        private static ClaimsPrincipal? ValidateBearer(McpAuthOptions authOptions)
        {
            var token = ResolveCredential(authOptions);
            if (string.IsNullOrEmpty(token))
                return null;

            // Token-exchange store is the opt-in OIDC path: when configured it, not the static
            // validator, resolves the candidate principal. It returns null on a failed/unknown
            // exchange (fail closed), mirroring IPgCredentialStore — never an ambient identity.
            // The store's exchange is async (real IdP calls are network I/O), but the per-tool-call
            // userContextProvider seam this runs under is synchronous; bridge at that boundary.
            if (authOptions.CredentialStore is not null)
                return authOptions.CredentialStore
                    .ExchangeAsync(token, CancellationToken.None)
                    .GetAwaiter().GetResult();

            if (authOptions.ValidateBearerToken is null)
                return null;
            return authOptions.ValidateBearerToken(token);
        }

        /// <summary>
        /// Resolves the HTTP transport's per-session principal by extracting the bearer from the
        /// session-initiating request's <c>Authorization</c> header (slice C's
        /// <see cref="McpCredentialSources.ExtractBearerToken"/> seam) and running the async
        /// credential exchange / validation. Unlike <see cref="ValidateBearer"/> — the synchronous
        /// stdio path — this is <b>awaited end to end</b>: the token-exchange I/O never crosses a
        /// <c>GetAwaiter().GetResult()</c> bridge on the context-bearing ASP.NET path (see
        /// docs/solutions/bifrostql/mcp-slice-d-oidc-credential-store-async-bridge). An absent header,
        /// a non-bearer mode, or a failed/unknown exchange yields <c>null</c> — no identity, never an
        /// anonymous one. The resolved principal is still projected through
        /// <see cref="IBifrostAuthContextFactory"/> (which rejects an unmapped issuer, fail closed).
        /// </summary>
        internal static async Task<ClaimsPrincipal?> ResolveBearerPrincipalAsync(
            McpAuthOptions authOptions, string? authorizationHeaderValue, CancellationToken cancellationToken)
        {
            if (authOptions.Mode != McpAuthMode.Bearer)
                return null;

            var token = McpCredentialSources.ExtractBearerToken(authorizationHeaderValue);
            if (string.IsNullOrEmpty(token))
                return null;

            if (authOptions.CredentialStore is not null)
                return await authOptions.CredentialStore.ExchangeAsync(token, cancellationToken).ConfigureAwait(false);

            return authOptions.ValidateBearerToken?.Invoke(token);
        }

        /// <summary>
        /// Builds a projection-ONLY user-context provider for one HTTP session from an
        /// already-resolved principal (no credential exchange runs inside it). HTTP identity is
        /// fixed for the session by the bearer presented at the initialize request, so the
        /// principal is projected through <paramref name="authContextFactory"/> ONCE here — while
        /// <paramref name="services"/> is a LIVE scope — and each tool call returns a fresh copy of
        /// that snapshot. Snapshotting is essential: the MCP session outlives the initiating request
        /// whose scope this projection reads (the OIDC claim-mapper registry), so deferring the
        /// projection to tool-call time would touch a disposed scope on later requests. A null
        /// principal snapshots an empty (fail-closed) context; an unmapped issuer throws here and the
        /// throw is deferred to each tool call so the handler sanitizes it onto the wire — never an
        /// empty/anonymous context.
        /// </summary>
        internal static Func<IDictionary<string, object?>> CreateProjectionProvider(
            IBifrostAuthContextFactory authContextFactory, IServiceProvider services, ClaimsPrincipal? principal)
        {
            var carrier = new DefaultHttpContext { RequestServices = services };
            if (principal is not null)
                carrier.User = principal;

            IDictionary<string, object?> projected;
            try
            {
                projected = authContextFactory.CreateUserContext(carrier);
            }
            catch (UnmappedOidcIssuerException ex)
            {
                // Fail closed, sanitized: rethrow on every tool call so the handler maps it to a
                // generic MCP error (invariant 3) instead of ever yielding an anonymous context.
                return () => throw ex;
            }

            var frozen = new Dictionary<string, object?>(projected);
            return () => new Dictionary<string, object?>(frozen);
        }

        /// <summary>
        /// Reads the raw credential for the session/request. The per-transport
        /// <see cref="McpAuthOptions.CredentialSource"/> is authoritative when configured — it
        /// alone knows WHERE the credential lives (stdio env/handshake vs HTTP Authorization
        /// header); the static <see cref="McpAuthOptions.BearerToken"/> is the fallback for a host
        /// that presents one token directly.
        /// </summary>
        private static string? ResolveCredential(McpAuthOptions authOptions)
            => authOptions.CredentialSource is not null
                ? authOptions.CredentialSource()
                : authOptions.BearerToken;

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

            if (authOptions.EnableWrites)
                logger.LogWarning(
                    "MCP front door started with WRITES ENABLED — the bifrost_insert, bifrost_update, and " +
                    "bifrost_delete tools are exposed. Writes route through the full mutation pipeline (tenant " +
                    "scoping, soft-delete, audit); this is a deliberate opt-in, off by default.");

            logger.LogInformation(
                "MCP front door ready (auth mode: {Mode}, writes: {Writes}).",
                authOptions.Mode, authOptions.EnableWrites ? "enabled" : "disabled");
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
                userContextProvider: ConfigureAuth(),
                mutationExecutor: _mutationExecutor,
                enableWrites: _authOptions.EnableWrites,
                toolPolicy: _authOptions.ToolPolicy,
                logger: _logger);
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

    /// <summary>
    /// Per-transport credential-read steps for <see cref="McpAuthOptions.CredentialSource"/>. Each
    /// factory returns a <see cref="Func{TResult}"/> that reads the raw credential from ONE
    /// transport's carrier and nothing else — it never validates the token, reads a claim, or
    /// projects identity (that stays with <see cref="McpAuthOptions.ValidateBearerToken"/> and the
    /// shared <see cref="IBifrostAuthContextFactory"/>). Swapping stdio for HTTP swaps only which
    /// factory built the source; the projection downstream is identical.
    /// </summary>
    public static class McpCredentialSources
    {
        /// <summary>
        /// stdio credential-read: a process-scoped bearer token carried in an environment variable
        /// (a stdio session has no per-request principal — the caller is whoever launched the
        /// process). Re-read on each invocation. An unset variable presents no credential.
        /// </summary>
        public static Func<string?> FromEnvironment(string variableName)
        {
            ArgumentException.ThrowIfNullOrEmpty(variableName);
            return () => Environment.GetEnvironmentVariable(variableName);
        }

        /// <summary>
        /// HTTP credential-read (consumed by the HTTP transport slice, not wired here): extracts the
        /// bearer token from the value produced by <paramref name="authorizationHeaderAccessor"/>
        /// (the request's <c>Authorization</c> header). The accessor is the ONLY HTTP-specific
        /// coupling; the extracted token flows through the same projection as every other transport.
        /// </summary>
        public static Func<string?> FromAuthorizationHeader(Func<string?> authorizationHeaderAccessor)
        {
            ArgumentNullException.ThrowIfNull(authorizationHeaderAccessor);
            return () => ExtractBearerToken(authorizationHeaderAccessor());
        }

        /// <summary>
        /// Parses the token out of an <c>Authorization: Bearer &lt;token&gt;</c> header value. The
        /// scheme is matched case-insensitively and the token is trimmed; anything that is not a
        /// well-formed non-empty Bearer credential returns <c>null</c> (no credential → fail closed).
        /// </summary>
        public static string? ExtractBearerToken(string? authorizationHeaderValue)
        {
            if (string.IsNullOrWhiteSpace(authorizationHeaderValue))
                return null;

            const string scheme = "Bearer ";
            if (!authorizationHeaderValue.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return null;

            var token = authorizationHeaderValue.Substring(scheme.Length).Trim();
            return token.Length == 0 ? null : token;
        }
    }
}
