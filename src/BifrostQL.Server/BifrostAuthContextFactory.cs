using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Server
{
    /// <summary>
    /// Builds the Bifrost user context for a request from its authenticated principal.
    /// Every transport gate (HTTP GraphQL middleware, binary WebSocket middleware,
    /// protocol-frontend middleware, and sidecar workflow endpoints) resolves identity
    /// through this single service so the projection of <c>HttpContext.User</c> into the
    /// user context — and its fail-closed semantics — can never drift between gates:
    /// an authenticated principal yields the full claim projection, an unauthenticated
    /// request yields an empty context, and a token from an unmapped OIDC issuer throws
    /// (the caller translates that to 403/error, never a degraded identity).
    /// Every shipped gate awaits <see cref="CreateUserContextAsync"/>, the projection that can run a
    /// registered <see cref="IGrantResolver"/>; a host factory or a third-party adapter does the same.
    /// The synchronous <see cref="CreateUserContext"/> cannot await the resolver, so with one
    /// registered the default factory's sync call refuses an authenticated caller (throws) rather
    /// than block on it.
    /// </summary>
    public interface IBifrostAuthContextFactory
    {
        /// <summary>
        /// Builds the user context for <paramref name="context"/>. An authenticated
        /// principal is projected into the full Bifrost user context; an unauthenticated
        /// request yields an empty, mutable dictionary. Throws when the principal carries
        /// an OIDC issuer this deployment has no claim mapper for (fail closed).
        /// Every projection seam copies the result through <see cref="UserContexts.Copy"/>, so keys
        /// compare case-insensitively whatever comparer the returned dictionary uses; one holding
        /// two keys that differ only in case is refused like an unprojectable identity.
        /// </summary>
        IDictionary<string, object?> CreateUserContext(HttpContext context);

        /// <summary>
        /// The asynchronous projection every Bifrost gate awaits, with the same contract as
        /// <see cref="CreateUserContext"/>. The default factory awaits the registered
        /// <see cref="IGrantResolver"/> here, resolving it (and the scoped services it reads) from
        /// <paramref name="services"/>: pass <c>context.RequestServices</c> for an ordinary request,
        /// or the scope that owns the unit of work (the binary transport passes each message's
        /// scope). A factory that decorates another awaits the inner one's
        /// <c>CreateUserContextAsync</c>, so the resolver still runs. The default implementation
        /// calls <see cref="CreateUserContext"/>, which keeps a sync-only factory working unchanged.
        /// </summary>
        ValueTask<IDictionary<string, object?>> CreateUserContextAsync(
            HttpContext context, IServiceProvider? services, CancellationToken cancellationToken)
            => new(CreateUserContext(context));

        // NOTE: the former merge overload — CreateUserContext(context, existing) — was
        // REMOVED, not deprecated. It stripped identity-owned keys using only the DEFAULT
        // IdentityContextMapper key names, so a deployment-configured tenant-context-key
        // (e.g. org_id) could still be smuggled from the wire. Frontend-parsed context now
        // rides BifrostRequest.WireContext and is merged model-aware by
        // BifrostQL.Core.Auth.WireContextMerger after the engine resolves the model.
    }

    /// <summary>
    /// Default <see cref="IBifrostAuthContextFactory"/>. Stateless; the identity
    /// projection itself lives in <see cref="BifrostContext"/>, which reads the OIDC
    /// claim-mapper registry from the request's own service provider.
    /// </summary>
    internal sealed class BifrostAuthContextFactory : IBifrostAuthContextFactory
    {
        /// <summary>Shared stateless instance used when no override is registered.</summary>
        internal static readonly BifrostAuthContextFactory Instance = new();

        /// <summary>
        /// Resolves the factory for a request: a DI-registered override when present
        /// (registered by <see cref="BifrostServiceRegistrar"/>), otherwise the shared
        /// default. Request-time resolution keeps the transport middlewares' public
        /// constructors unchanged and covers hosts that mount a transport without the
        /// full AddBifrostQL registration.
        /// </summary>
        internal static IBifrostAuthContextFactory Resolve(HttpContext context)
            => Resolve(context.RequestServices);

        /// <summary>The factory registered in <paramref name="services"/>, else the shared default.</summary>
        internal static IBifrostAuthContextFactory Resolve(IServiceProvider? services)
            => services?.GetService<IBifrostAuthContextFactory>() ?? Instance;

        /// <summary>
        /// The synchronous public projection. It cannot run a registered
        /// <see cref="IGrantResolver"/>: the resolver is asynchronous, and finishing it here
        /// would block the calling thread. With a resolver registered in
        /// <c>context.RequestServices</c> an authenticated caller is therefore refused (throws,
        /// which every gate maps to an unprojectable identity) rather than served without its
        /// grants. Bifrost's own transport gates await <see cref="CreateUserContextAsync"/>,
        /// which runs the resolver.
        /// </summary>
        public IDictionary<string, object?> CreateUserContext(HttpContext context)
        {
            if (!IsAuthenticated(context))
                return new Dictionary<string, object?>();
            if (context.RequestServices?.GetService<IGrantResolver>() is not null)
                throw new InvalidOperationException(
                    "An IGrantResolver is registered, and the synchronous CreateUserContext cannot run it " +
                    "without blocking. The caller is refused; Bifrost's transport gates project asynchronously.");
            return new BifrostContext(context);
        }

        /// <summary>
        /// Awaits the registered grant resolver, taking scoped services (the resolver and the
        /// DbContext it reads) from <paramref name="services"/> rather than the request's own
        /// scope: the binary transport passes each message's scope, and the raw-wire adapters a
        /// per-login scope.
        /// </summary>
        public async ValueTask<IDictionary<string, object?>> CreateUserContextAsync(
            HttpContext context, IServiceProvider? services, CancellationToken cancellationToken)
        {
            if (!IsAuthenticated(context))
                return new Dictionary<string, object?>();

            var userContext = new BifrostContext(context);
            var resolver = services?.GetService<IGrantResolver>();
            if (resolver is not null)
                await ApplyGrantResolverAsync(context, services!, resolver, userContext, cancellationToken);
            return userContext;
        }

        private static bool IsAuthenticated(HttpContext context)
            => context.User?.Identity?.IsAuthenticated == true;

        /// <summary>
        /// The S2 grant-loading hook: when the host registered an
        /// <see cref="IGrantResolver"/> (<c>AddBifrostGrantResolver</c>), await it once for
        /// this projection and UNION its grants into the <c>permissions</c> context key —
        /// after the identity mapping (which ran inside <see cref="BifrostContext"/>) and
        /// before any transformer sees the context. Runs for authenticated principals
        /// only; the identity handed to the resolver is the canonical
        /// <see cref="PolicyIdentity.FromUserContext"/> projection of the just-assembled
        /// context, so the resolver sees the id, roles and permissions the security modules
        /// will see. The tenant is the gap: this runs before any endpoint's model is
        /// resolved, so no <c>tenant-context-key</c> is known and <c>TenantId</c> is the
        /// identity's own tenant (<c>tenant_id</c>). On a host whose
        /// <c>tenant-context-key</c> names a raw claim (e.g. <c>org_id</c>), the resolver
        /// gets <c>TenantId = null</c> while the tenant filter scopes by that claim; it reads
        /// the claim from <see cref="AppIdentity.Claims"/>, which holds the principal's claims.
        ///
        /// Fail closed (E9): a throwing resolver leaves the caller with an EMPTY
        /// permission set — the pre-resolver permissions are wiped — and logs a Warning
        /// naming the identity id; the exception never escapes, except any exception thrown while
        /// <c>cancellationToken</c> is cancelled, which propagates as an
        /// <see cref="OperationCanceledException"/> on that token, whatever its own type (every gate
        /// maps it to a refused identity) so a caller-cancelled projection is never served. A null result is an
        /// empty grant set: nothing is unioned, the identity's own permissions remain.
        /// </summary>
        private static async ValueTask ApplyGrantResolverAsync(
            HttpContext context, IServiceProvider services, IGrantResolver resolver,
            IDictionary<string, object?> userContext, CancellationToken cancellationToken)
        {
            // The factory runs before any model is resolved, so the tenant comes from the key the
            // identity mapper writes it under (tenant_id), the identity's own tenant. The key-less
            // overload projects no tenant, which handed every resolver TenantId = null.
            // The resolver is handed no principal (and pgwire/RESP/LDAP have no
            // IHttpContextAccessor), so the identity carries the principal's raw claims, one
            // string[] per claim type, keyed case-insensitively like the user context.
            var identity = PolicyIdentity.FromUserContext(
                userContext, MetadataKeys.Auth.DefaultTenantContextKey, RawClaims(context.User));
            IReadOnlyCollection<string>? grants;
            try
            {
                grants = await resolver.ResolveAsync(identity, cancellationToken);
            }
            // The caller's own cancellation propagates, whatever the resolver threw for it (SqlClient
            // raises SqlException, not OperationCanceledException): a projection degraded by it is
            // no identity. Keyed on the token, not the exception type. A resolver's internal
            // timeout, under a live caller token, is a throw like any other.
            // Surfaced as OperationCanceledException on the caller's token, so every seam (MCP
            // catches only its own refusal types) sees a cancellation it already handles, never a
            // driver exception. The resolver's own exception stays as the inner one.
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
            {
                throw new OperationCanceledException(
                    "The grant resolver failed after the caller cancelled the projection.", ex, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                services.GetService<ILoggerFactory>()
                    ?.CreateLogger("BifrostQL.Server.BifrostAuthContextFactory")
                    .LogWarning(ex,
                        "Grant resolver failed for identity '{IdentityId}'; continuing with an empty permission set.",
                        identity.Id);
                userContext[MetadataKeys.Auth.DefaultPermissionsContextKey] = Array.Empty<string>();
                return;
            }

            if (grants is null || grants.Count == 0)
                return;

            IdentityContextMapper.UnionPermissions(userContext, grants);
        }

        private static IReadOnlyDictionary<string, object?> RawClaims(System.Security.Claims.ClaimsPrincipal principal)
            => principal.Claims
                .GroupBy(claim => claim.Type, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (object?)group.Select(claim => claim.Value).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

    }
}
