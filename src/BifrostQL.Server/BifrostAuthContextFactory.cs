using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
    /// </summary>
    public interface IBifrostAuthContextFactory
    {
        /// <summary>
        /// Builds the user context for <paramref name="context"/>. An authenticated
        /// principal is projected into the full Bifrost user context; an unauthenticated
        /// request yields an empty, mutable dictionary. Throws when the principal carries
        /// an OIDC issuer this deployment has no claim mapper for (fail closed).
        /// </summary>
        IDictionary<string, object?> CreateUserContext(HttpContext context);

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
            => context.RequestServices?.GetService<IBifrostAuthContextFactory>() ?? Instance;

        public IDictionary<string, object?> CreateUserContext(HttpContext context)
        {
            var user = context.User;
            if (user?.Identity?.IsAuthenticated == true)
                return new BifrostContext(context);

            return new Dictionary<string, object?>();
        }

    }
}
