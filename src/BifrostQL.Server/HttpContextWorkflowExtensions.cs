using Microsoft.AspNetCore.Http;

namespace BifrostQL.Server
{
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
        /// writes. A projection fault (unmapped OIDC issuer, subject-less
        /// principal) also yields the empty context — fail closed, no identity —
        /// rather than escaping as an unhandled exception. A sidecar mounting a
        /// workflow endpoint must gate the request itself before calling this.
        /// </summary>
        public static IDictionary<string, object?> GetBifrostUserContext(this HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            BifrostIdentityGate.Project(context, out var userContext);
            return userContext;
        }
    }
}
