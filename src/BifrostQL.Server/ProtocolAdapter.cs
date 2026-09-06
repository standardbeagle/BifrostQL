using Microsoft.Extensions.Hosting;

namespace BifrostQL.Server
{
    /// <summary>
    /// Hosting contract for a non-GraphQL protocol front end (OData, gRPC, custom
    /// binary framing, in-process pipes, …).
    ///
    /// <para><b>Responsibility split.</b> The adapter owns the wire: listening for
    /// requests in its own protocol and the codec between that wire format and
    /// Bifrost's programmatic surface. Core owns the data path: an adapter takes
    /// <see cref="BifrostQL.Core.Resolvers.IQueryIntentExecutor"/> and
    /// <see cref="IBifrostAuthContextFactory"/> from DI, resolves caller identity
    /// through the factory, and executes reads through the intent executor — never
    /// through <c>SqlExecutionManager</c> or a database connection directly. The
    /// intent executor is the only execution surface, so the security transformer
    /// pipeline (tenant isolation, soft-delete, policy row scope, column read
    /// guards) applies to every adapter request with no way around it.</para>
    ///
    /// <para><b>Lifecycle.</b> Register with
    /// <c>AddBifrostQL(o =&gt; o.AddProtocolAdapter&lt;T&gt;())</c> (or the
    /// multi-database <c>BifrostMultiDbOptions</c> equivalent). Each adapter is
    /// wrapped in its own <see cref="IHostedService"/>: the host calls
    /// <see cref="StartAsync"/> during startup and <see cref="StopAsync"/> during
    /// graceful shutdown. An exception thrown from <see cref="StartAsync"/> aborts
    /// host startup — fail fast, do not swallow bind/listen errors. Adapters must
    /// not depend on <c>HttpContext</c>; identity arrives on the adapter's own wire
    /// and is projected via <see cref="IBifrostAuthContextFactory"/>.</para>
    ///
    /// <para><b>Hosting a raw (non-HTTP) TCP protocol.</b> Do not open a bare
    /// <c>Socket</c> accept loop; bind the port through Kestrel's connection
    /// middleware and let the connection handler decode frames and delegate to the
    /// adapter:</para>
    /// <code>
    /// webBuilder.ConfigureKestrel(kestrel =&gt;
    ///     kestrel.ListenAnyIP(9090, listen =&gt;
    ///         listen.UseConnectionHandler&lt;MyProtocolConnectionHandler&gt;()));
    /// </code>
    /// <para>Kestrel then owns socket accept/backpressure/shutdown draining, and the
    /// adapter's <see cref="StartAsync"/>/<see cref="StopAsync"/> manage only the
    /// protocol-level state (codec tables, session registries, …).</para>
    /// </summary>
    public interface IProtocolAdapter
    {
        /// <summary>
        /// Begins accepting protocol requests. Called once by the host during
        /// startup; a thrown exception aborts host startup.
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Stops accepting protocol requests and drains in-flight work. Called once
        /// by the host during graceful shutdown.
        /// </summary>
        Task StopAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// One-per-adapter <see cref="IHostedService"/> wrapper: ties an
    /// <see cref="IProtocolAdapter"/>'s lifecycle to the host's without the adapter
    /// implementing hosting interfaces itself. Registered by
    /// <see cref="BifrostServiceRegistrar.RegisterProtocolAdapterServices"/>.
    /// </summary>
    internal sealed class ProtocolAdapterHostedService : IHostedService
    {
        private readonly IProtocolAdapter _adapter;

        public ProtocolAdapterHostedService(IProtocolAdapter adapter)
            => _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

        public Task StartAsync(CancellationToken cancellationToken) => _adapter.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => _adapter.StopAsync(cancellationToken);
    }

    /// <summary>
    /// The ONE fail-closed identity projection every raw-wire front door (pgwire, RESP, LDAP)
    /// uses. A verified credential proves only that the caller holds the secret; it does not by
    /// itself grant a Bifrost identity. The candidate principal the adapter's credential store
    /// returned is projected through the shared <see cref="IBifrostAuthContextFactory"/> — the
    /// same seam every HTTP and binary gate uses — and the login is refused unless that yields a
    /// NON-EMPTY user context.
    ///
    /// <para>An empty user context is not a refusal: it only gates tables that DECLARE tenant
    /// metadata, so every other table stays readable by a caller with no identity
    /// (protocol-adapter-security invariant 12). Both failure arms — a projection that throws
    /// (subject-less principal, unmapped OIDC issuer) and one that yields nothing — therefore
    /// return false and an EMPTY out-parameter the caller must not use, never a degraded
    /// anonymous session.</para>
    ///
    /// <para>One copy, deliberately: pgwire, RESP and LDAP each carried their own, and three
    /// copies of one fail-closed decision is three places for it to drift OPEN.</para>
    /// </summary>
    internal static class AdapterIdentityProjection
    {
        /// <param name="adapter">Front-door name for the server-side log line only; it never
        /// reaches a client wire.</param>
        public static bool TryProject(
            IBifrostAuthContextFactory authFactory,
            IServiceProvider services,
            System.Security.Claims.ClaimsPrincipal principal,
            Microsoft.Extensions.Logging.ILogger logger,
            string adapter,
            out IDictionary<string, object?> userContext)
        {
            userContext = new Dictionary<string, object?>();
            try
            {
                var carrier = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    RequestServices = services,
                    User = principal,
                };
                var projected = authFactory.CreateUserContext(carrier);
                if (projected.Count == 0)
                {
                    Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                        logger, "{Adapter} login projected to an empty user context; rejecting.", adapter);
                    return false;
                }
                userContext = projected;
                return true;
            }
            catch (Exception ex)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                    logger, ex, "{Adapter} identity projection failed; rejecting login.", adapter);
                return false;
            }
        }
    }
}
