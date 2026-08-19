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

        /// <summary>
        /// Builds the user context for <paramref name="context"/>, merging in
        /// <paramref name="existing"/> entries a protocol frontend already parsed from the
        /// request. Authenticated: identity-derived keys win; <paramref name="existing"/>
        /// entries are added only where no identity key exists. Unauthenticated: returns
        /// <paramref name="existing"/> when it has entries, otherwise an empty dictionary.
        /// </summary>
        IDictionary<string, object?> CreateUserContext(HttpContext context, IDictionary<string, object?> existing);
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

        public IDictionary<string, object?> CreateUserContext(HttpContext context, IDictionary<string, object?> existing)
        {
            var user = context.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                var bifrostContext = new BifrostContext(context);
                // Merge frontend-parsed entries, but NEVER let them supply an identity-owned
                // security key. Excluding only the keys currently PRESENT is not enough: the
                // identity mapper omits the tenant key when the identity has no tenant (and the
                // roles/permissions/tenant-ids keys are likewise identity-owned), so an absent
                // slot would be fillable from the wire — a tenant-less caller could inject its own
                // tenant. Exclude the mapper's whole owned-key set (BifrostContext builds the
                // identity with this same default mapper) plus the raw principal, regardless of
                // presence, so frontend context can only ever add NON-identity keys.
                var ownedKeys = new BifrostQL.Core.Auth.IdentityContextMapper().OwnedKeyNames;
                foreach (var kv in existing)
                {
                    if (bifrostContext.ContainsKey(kv.Key))
                        continue;
                    if (ownedKeys.Contains(kv.Key)
                        || string.Equals(kv.Key, "user", StringComparison.Ordinal))
                        continue;
                    bifrostContext[kv.Key] = kv.Value;
                }
                return bifrostContext;
            }

            return existing.Count > 0 ? existing : new Dictionary<string, object?>();
        }
    }
}
