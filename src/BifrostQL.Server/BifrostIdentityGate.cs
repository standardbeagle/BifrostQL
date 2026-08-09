using Microsoft.AspNetCore.Http;

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
    /// The one identity projection every non-GraphQL HTTP surface in this assembly runs its callers
    /// through. Both <see cref="BifrostSavedObjectsMiddleware"/> and
    /// <see cref="BifrostAppMetadataMiddleware"/> used to hand-roll this, and the two copies had
    /// already drifted: saved-objects caught the projection fault and answered 401, app-metadata let
    /// it escape to the host as a 500 with a stack trace wherever a developer exception page was
    /// enabled. Two copies of one security decision drift; one copy cannot.
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
            catch
            {
                // Unmapped issuer / malformed principal. Fail closed, and answer from the
                // middleware rather than letting the fault reach the host.
                return BifrostIdentityOutcome.Unprojectable;
            }

            if (projected.Count == 0)
                return BifrostIdentityOutcome.Anonymous;

            userContext = projected;
            return BifrostIdentityOutcome.Projected;
        }
    }
}
