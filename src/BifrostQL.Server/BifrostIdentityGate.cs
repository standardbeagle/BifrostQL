using BifrostQL.Core.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
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
        BifrostIdentityOutcome Outcome, IDictionary<string, object?> UserContext)
    {
        /// <summary>The grant resolver threw and the context carries the E9 empty permission set:
        /// served to this call, never reused by <see cref="IdentityProjectionMemo"/>.</summary>
        public bool GrantResolutionFailed { get; init; }
    }

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
        /// <para>Memoized in the request's DI scope (<see cref="HttpContext.RequestServices"/>), see
        /// <see cref="ProjectAsync(HttpContext, IServiceProvider?)"/>: an app endpoint that projects
        /// the caller and then asks <see cref="PolicyGateAccessor"/> for its gate runs the factory,
        /// and the grant resolver, once. Never memoized on a long-lived request
        /// (<see cref="OutlivesOneRequest"/>): that HttpContext and its RequestServices span the
        /// whole session, and identity is memoized per request, never per session
        /// (invariant 13).</para>
        /// </summary>
        internal static ValueTask<BifrostIdentityProjection> ProjectAsync(HttpContext context)
            => OutlivesOneRequest(context)
                ? ProjectFreshAsync(context, context.RequestServices)
                : ProjectAsync(context, context.RequestServices);

        /// <summary>
        /// True when <paramref name="context"/> serves many invocations: a WebSocket request, or an
        /// endpoint that opted out of request timeouts. ASP.NET's <c>MapConnections</c> (behind
        /// <c>MapHub</c> and <c>MapConnectionHandler</c>) puts <see cref="DisableRequestTimeoutAttribute"/>
        /// on the SignalR transport route, so this covers SSE and long polling, whose HttpContext is
        /// no WebSocket request (long polling's connection-lifetime clone carries the same endpoint).
        /// A host endpoint that disables its own request timeout is long-lived by its own
        /// declaration, and loses only the memo.
        /// </summary>
        private static bool OutlivesOneRequest(HttpContext context)
            => context.WebSockets.IsWebSocketRequest
               || context.GetEndpoint()?.Metadata.GetMetadata<DisableRequestTimeoutAttribute>() is not null;

        /// <summary>
        /// <see cref="ProjectAsync(HttpContext)"/> with the factory and the default factory's
        /// scoped services (grant resolver) taken from <paramref name="scope"/>, memoized in that
        /// scope's <see cref="IdentityProjectionMemo"/>: the binary transport passes each message's
        /// own scope, so the message's projection and a hook's <see cref="PolicyGateAccessor"/> in
        /// that scope share one resolver call, and the next message projects afresh. Without a
        /// scope, in a scope with no registered memo, or in the ROOT provider, the projection is not
        /// memoized: a scoped memo resolved from the root lives for the process, and a host whose
        /// auth handler hands one cached principal to every request would be served the first
        /// request's grants. The root is never asked for the scoped memo, so scope validation cannot
        /// turn that lookup into an unhandled fault; the fresh projection there fails closed when the
        /// scoped grant resolver cannot be resolved.
        /// </summary>
        internal static ValueTask<BifrostIdentityProjection> ProjectAsync(
            HttpContext context, IServiceProvider? scope)
        {
            if (scope is null || RootServiceProvider.IsRoot(scope)
                || !(context.User?.Identity?.IsAuthenticated ?? false)
                || scope.GetService<IdentityProjectionMemo>() is not { } memo)
                return ProjectFreshAsync(context, scope);
            return memo.ProjectAsync(context, scope, BifrostAuthContextFactory.Resolve(scope));
        }

        /// <summary>The unmemoized projection, with the factory and its scoped services taken from
        /// <paramref name="services"/>; <see cref="IdentityProjectionMemo"/> runs it once per scope.</summary>
        internal static async ValueTask<BifrostIdentityProjection> ProjectFreshAsync(
            HttpContext context, IServiceProvider? services)
        {
            if (!(context.User?.Identity?.IsAuthenticated ?? false))
                return new(BifrostIdentityOutcome.Anonymous, new Dictionary<string, object?>());

            IDictionary<string, object?> projected;
            bool grantResolutionFailed;
            try
            {
                // Normalised once, here: a host factory may return an ordinal dictionary, and the
                // surfaces that read the context directly must agree with the ones that copy it.
                // Keys differing only in case throw inside this try and are refused like any
                // other unprojectable identity. A snapshot, so the projection shares no collection
                // value (a host's List<string>, the roles/permissions arrays) with the factory or
                // with any other caller handed a copy of a memoized projection.
                var created = await BifrostAuthContextFactory.Resolve(services).CreateUserContextAsync(
                    context, services, context.RequestAborted);
                // Read before the snapshot drops the type. A host factory that decorates the
                // default one and returns a copy loses the flag, and its degraded projection is
                // memoized for the scope as before.
                grantResolutionFailed = created is BifrostContext { GrantResolutionFailed: true };
                projected = UserContexts.Snapshot(created);
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
                : new(BifrostIdentityOutcome.Projected, projected) { GrantResolutionFailed = grantResolutionFailed };
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
