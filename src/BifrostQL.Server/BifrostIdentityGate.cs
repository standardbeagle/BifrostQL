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
        /// closed. <paramref name="userContext"/> is the projected context for
        /// <see cref="BifrostIdentityOutcome.Projected"/> and an empty dictionary otherwise, so a
        /// caller that ignores the outcome still cannot read an identity out of a refused request.
        /// </summary>
        internal static BifrostIdentityOutcome Project(
            HttpContext context, out IDictionary<string, object?> userContext)
        {
            userContext = new Dictionary<string, object?>();

            if (!(context.User?.Identity?.IsAuthenticated ?? false))
                return BifrostIdentityOutcome.Anonymous;

            IDictionary<string, object?> projected;
            try
            {
                projected = BifrostAuthContextFactory.Resolve(context).CreateUserContext(context);
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
                return BifrostIdentityOutcome.Unprojectable;
            }

            if (projected.Count == 0)
                return BifrostIdentityOutcome.Anonymous;

            userContext = projected;
            return BifrostIdentityOutcome.Projected;
        }
    }
}
