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
        /// GraphQL middleware builds for a direct <c>/graphql</c> request. A
        /// workflow endpoint passes this straight to
        /// <see cref="IBifrostWorkflowExecutor"/> so its operations run as the
        /// caller; identity is reused, never re-derived. An unauthenticated
        /// request yields an empty context.
        /// </summary>
        public static IDictionary<string, object?> GetBifrostUserContext(this HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var user = context.User;
            if (user?.Identity?.IsAuthenticated == true)
                return new BifrostContext(context);

            return new Dictionary<string, object?>();
        }
    }
}
