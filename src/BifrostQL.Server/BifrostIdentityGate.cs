using BifrostQL.Core.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
    /// <summary>
    /// How a request's caller projected through the shared <see cref="IBifrostAuthContextFactory"/>.
    /// </summary>
    internal enum BifrostIdentityOutcome
    {
        /// <summary>
        /// No usable Bifrost identity: the request is unauthenticated, or the principal projected
        /// to an EMPTY user context. The empty projection is the factory's fail-closed signal — a
        /// subject-less principal, or one whose claims map to nothing — and is deliberately NOT
        /// distinguished from anonymous here, because no gate may treat it as an identity.
        /// </summary>
        Anonymous,

        /// <summary>
        /// The projection FAULTED: a token from an OIDC issuer this deployment registered no claim
        /// mapper for, or a malformed principal. Never authorized, and never allowed to escape to
        /// the host as an unhandled fault (.claude/rules/protocol-adapter-security.md invariant 1).
        /// </summary>
        Unprojectable,

        /// <summary>The caller carries a real, non-empty Bifrost user context.</summary>
        Projected,
    }

    /// <summary>The outcome of <see cref="BifrostIdentityGate.ProjectAsync(HttpContext)"/> and the
    /// context it yields: empty for every outcome but <see cref="BifrostIdentityOutcome.Projected"/>.</summary>
    internal readonly record struct BifrostIdentityProjection(
        BifrostIdentityOutcome Outcome, IDictionary<string, object?> UserContext);

    /// <summary>
    /// The one identity projection every HTTP surface in this assembly runs its callers
    /// through (M13) — the GraphQL mount (<see cref="BifrostHttpMiddleware"/>), the
    /// protocol-frontend mount (<see cref="BifrostFrontendMiddleware"/>), the binary
    /// WebSocket mount (<see cref="BifrostBinaryMiddleware"/>), the chat mount
    /// (<see cref="BifrostChatMiddleware"/>) and the workflow sidecar helper
    /// (<see cref="HttpContextWorkflowExtensions"/>). Hand-rolled copies of this projection
    /// drifted: saved-objects caught the projection fault and answered 401 while
    /// app-metadata let it escape to the host as a 500, and the GraphQL mount caught only
    /// <c>UnmappedOidcIssuerException</c> while a subject-less principal escaped as
    /// <c>InvalidOperationException</c>. Two copies of one security decision drift; one
    /// copy cannot. Non-HTTP protocol adapters (LDAP/RESP/pgwire/S3/OData/Feeds/Prometheus/
    /// gRPC) project their own carrier types through the same factory under their own
    /// per-adapter funnels.
    /// </summary>
    internal static class BifrostIdentityGate
    {
        /// <summary>
        /// Projects <paramref name="context"/>'s caller through the SHARED
        /// <see cref="IBifrostAuthContextFactory"/> — never a second identity rule — and fails
        /// closed. The projection's <c>UserContext</c> is the projected context for
        /// <see cref="BifrostIdentityOutcome.Projected"/> and an empty dictionary otherwise, so a
        /// caller that ignores the outcome still cannot read an identity out of a refused request.
        /// Asynchronous because the default factory awaits the host's grant resolver here; it
        /// runs only on the requests a mount actually serves, never on health or static routes.
        ///
        /// <para>Memoized for the request (invariant 13): an app endpoint that projects the caller
        /// and then asks <see cref="PolicyGateAccessor"/> for its gate runs the factory, and the
        /// grant resolver, once. The memo lives in <see cref="HttpContext.Items"/> under a private
        /// key, so it dies with the request, and it is reused only while <c>context.User</c> and
        /// the resolved factory are the same instances it was built from. Only a projected identity
        /// is memoized, never a refusal, and every caller gets its own copy of the context.</para>
        /// </summary>
        internal static async ValueTask<BifrostIdentityProjection> ProjectAsync(HttpContext context)
        {
            var factory = BifrostAuthContextFactory.Resolve(context.RequestServices);
            if (context.Items.TryGetValue(MemoKey, out var held) && held is RequestProjectionMemo memo
                && ReferenceEquals(memo.User, context.User) && ReferenceEquals(memo.Factory, factory))
                return new(BifrostIdentityOutcome.Projected, UserContexts.Copy(memo.UserContext));

            var projection = await ProjectAsync(context, context.RequestServices);
            if (projection.Outcome == BifrostIdentityOutcome.Projected)
                context.Items[MemoKey] = new RequestProjectionMemo(
                    context.User, factory, UserContexts.Copy(projection.UserContext));
            return projection;
        }

        /// <summary>The <see cref="HttpContext.Items"/> key of the per-request memo; private, so no
        /// other code can read or plant it.</summary>
        private static readonly object MemoKey = new();

        private sealed record RequestProjectionMemo(
            System.Security.Claims.ClaimsPrincipal User, IBifrostAuthContextFactory Factory,
            IDictionary<string, object?> UserContext);

        /// <summary>
        /// <see cref="ProjectAsync(HttpContext)"/> with the factory and the default factory's
        /// scoped services (grant resolver) taken from <paramref name="services"/>: the binary
        /// transport passes each message's own scope. Never memoized: a WebSocket's HttpContext
        /// spans the whole session, and each message projects afresh.
        /// </summary>
        internal static async ValueTask<BifrostIdentityProjection> ProjectAsync(
            HttpContext context, IServiceProvider? services)
        {
            if (!(context.User?.Identity?.IsAuthenticated ?? false))
                return new(BifrostIdentityOutcome.Anonymous, new Dictionary<string, object?>());

            IDictionary<string, object?> projected;
            try
            {
                // Normalised once, here: a host factory may return an ordinal dictionary, and the
                // surfaces that read the context directly must agree with the ones that copy it.
                // Keys differing only in case throw inside this try and are refused like any
                // other unprojectable identity.
                projected = UserContexts.Copy(await BifrostAuthContextFactory.Resolve(services).CreateUserContextAsync(
                    context, services, context.RequestAborted));
            }
            catch (Exception ex)
            {
                // Unmapped issuer / malformed principal. Fail closed, and answer from the
                // middleware rather than letting the fault reach the host. The wire gets a
                // constant 403; the diagnosable detail (issuer, missing claim) goes ONLY to
                // the server log, so a refused identity is never silent server-side.
                context.RequestServices?.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(BifrostIdentityGate))
                    .LogWarning(ex, "Caller identity could not be projected; refusing the request.");
                return new(BifrostIdentityOutcome.Unprojectable, new Dictionary<string, object?>());
            }

            return projected.Count == 0
                ? new(BifrostIdentityOutcome.Anonymous, new Dictionary<string, object?>())
                : new(BifrostIdentityOutcome.Projected, projected);
        }

        /// <summary>
        /// The one refusal rule for an HTTP surface with a <c>RequireAuth</c> switch: a projection
        /// fault is always refused (opting out of authentication is not opting into a broken
        /// credential), and when auth is required anything short of a real, non-empty identity is
        /// refused too. An authenticated principal that projects to an empty context is
        /// <see cref="BifrostIdentityOutcome.Anonymous"/>, not an identity (invariant 12).
        /// </summary>
        internal static bool Refuses(BifrostIdentityOutcome outcome, bool requireAuth) =>
            outcome == BifrostIdentityOutcome.Unprojectable
            || (requireAuth && outcome != BifrostIdentityOutcome.Projected);
    }
}
