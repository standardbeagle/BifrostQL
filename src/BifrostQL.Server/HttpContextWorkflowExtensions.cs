using Microsoft.AspNetCore.Http;

namespace BifrostQL.Server
{
    /// <summary>
    /// Raised by <see cref="HttpContextWorkflowExtensions.GetBifrostUserContextAsync"/> when the
    /// request carries an AUTHENTICATED principal that the shared identity projection refuses
    /// (a token from an OIDC issuer this deployment has no claim mapper for, or a principal
    /// with no subject claim). The message is a constant and carries no issuer, claim or
    /// principal detail (.claude/rules/protocol-adapter-security.md invariant 3). A sidecar
    /// endpoint catches it and answers 403 — the same status the HTTP mounts answer for the
    /// same condition (invariant 9).
    /// </summary>
    public sealed class BifrostIdentityRejectedException : Exception
    {
        /// <summary>The one wire-safe message for a refused identity.</summary>
        public const string WireMessage = "The caller identity is not accepted by this deployment.";

        public BifrostIdentityRejectedException() : base(WireMessage)
        {
        }
    }

    /// <summary>
    /// Extensions that expose the request's resolved Bifrost user context to
    /// sidecar workflow endpoints.
    /// </summary>
    public static class HttpContextWorkflowExtensions
    {
        /// <summary>
        /// Returns the Bifrost user context for the current request — the same
        /// projection of the authenticated <c>ClaimsPrincipal</c> that the
        /// GraphQL middleware builds for a direct <c>/graphql</c> request,
        /// projected through the one shared <see cref="BifrostIdentityGate"/>
        /// (M13: every HTTP-mount projection lives there). A workflow endpoint
        /// passes this straight to <see cref="IBifrostWorkflowExecutor"/> so its
        /// operations run as the caller; identity is reused, never re-derived.
        /// An unauthenticated request yields an EMPTY context, which is not a
        /// refusal — it gates only tables declaring tenant metadata, and
        /// <see cref="IBifrostWorkflowExecutor"/> null-checks the context but
        /// never requires it to be non-empty, so an empty one reaches reads AND
        /// writes. A sidecar mounting a workflow endpoint must gate the request
        /// itself before calling this. A projection FAULT (unmapped OIDC issuer,
        /// subject-less principal) throws <see cref="BifrostIdentityRejectedException"/>:
        /// an authenticated caller Bifrost cannot identify is never served as
        /// anonymous, because an empty context is not a refusal (invariant 12)
        /// and the sidecar's own <c>IsAuthenticated</c> gate has already let the
        /// principal through. Catch it and answer 403. Asynchronous because the
        /// projection awaits the host's grant resolver (<c>AddBifrostGrantResolver</c>).
        /// The projection is memoized in the request's DI scope
        /// (<see cref="HttpContext.RequestServices"/>), so the grant resolver runs once per
        /// request even when the endpoint also awaits <c>IPolicyGateAccessor</c>; on a WebSocket
        /// request it is never memoized. A SignalR hub or any endpoint that serves many
        /// invocations on one long-lived HttpContext calls the overload that takes the
        /// invocation's scope instead.
        /// </summary>
        public static Task<IDictionary<string, object?>> GetBifrostUserContextAsync(this HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToUserContextAsync(BifrostIdentityGate.ProjectAsync(context));
        }

        /// <summary>
        /// <see cref="GetBifrostUserContextAsync(HttpContext)"/> projected in
        /// <paramref name="scope"/>: the grant resolver (and the DbContext it reads) come from that
        /// scope, and the projection is memoized there. A SignalR hub passes its invocation's
        /// <see cref="IServiceProvider"/> (inject it into the hub), because the connection's
        /// HttpContext and its RequestServices live for the whole connection, and a memo there
        /// would serve a revoked grant until reconnect.
        /// </summary>
        public static Task<IDictionary<string, object?>> GetBifrostUserContextAsync(
            this HttpContext context, IServiceProvider scope)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(scope);
            return ToUserContextAsync(BifrostIdentityGate.ProjectAsync(context, scope));
        }

        private static async Task<IDictionary<string, object?>> ToUserContextAsync(
            ValueTask<BifrostIdentityProjection> projection)
        {
            var (outcome, userContext) = await projection;
            if (outcome == BifrostIdentityOutcome.Unprojectable)
                throw new BifrostIdentityRejectedException();
            return userContext;
        }
    }
}
