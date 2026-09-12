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
            {
                var userContext = new BifrostContext(context);
                ApplyGrantResolver(context, userContext);
                return userContext;
            }

            return new Dictionary<string, object?>();
        }

        /// <summary>
        /// The S2 grant-loading hook: when the host registered an
        /// <see cref="IGrantResolver"/> (<c>AddBifrostGrantResolver</c>), run it once for
        /// this request and UNION its grants into the <c>permissions</c> context key —
        /// after the identity mapping (which ran inside <see cref="BifrostContext"/>) and
        /// before any transformer sees the context. Runs for authenticated principals
        /// only; the identity handed to the resolver is the canonical
        /// <see cref="PolicyIdentity.FromUserContext"/> projection of the just-assembled
        /// context, so the resolver sees exactly what the security modules will see.
        ///
        /// Fail closed (E9): a throwing resolver leaves the request with an EMPTY
        /// permission set — the pre-resolver permissions are wiped — and logs a Warning
        /// naming the identity id; the exception never escapes. A null result is an
        /// empty grant set: nothing is unioned, the identity's own permissions remain.
        /// </summary>
        private static void ApplyGrantResolver(HttpContext context, IDictionary<string, object?> userContext)
        {
            var resolver = context.RequestServices?.GetService<IGrantResolver>();
            if (resolver is null)
                return;

            var identity = PolicyIdentity.FromUserContext(userContext);
            IReadOnlyCollection<string>? grants;
            try
            {
                // The factory contract is synchronous (every transport gate consumes it
                // that way); the resolver runs to completion inline. A resolver is
                // expected to do one bounded DB read per request.
                grants = resolver.ResolveAsync(identity, context.RequestAborted)
                    .AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                context.RequestServices?.GetService<ILoggerFactory>()
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

    }
}
