using System.Security.Claims;
using BifrostQL.Server.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server
{
    public static class UIAuthMiddleware
    {
        public static IApplicationBuilder UseUiAuth(this IApplicationBuilder app)
        {
            app.Use(async (context, next) =>
            {
                if ((context.User?.Identity?.IsAuthenticated ?? false) == false)
                {
                    // API/Bearer clients must get a 401, not an interactive OIDC 302 redirect
                    // to a login page they cannot follow. Only browser-style requests get the
                    // OIDC challenge. This keeps `Authorization: Bearer` and JSON API callers
                    // on a proper 401 while the interactive UI still redirects to login.
                    if (IsApiClient(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }

                    await context.ChallengeAsync("oauth2", new AuthenticationProperties()
                    {
                        RedirectUri = "/"
                    });
                }
                else
                {
                    if (await NormalizeOidcPrincipalAsync(context))
                        await next.Invoke(context);
                }
            });
            return app;
        }

        /// <summary>
        /// Whether the request is a non-interactive API client (as opposed to a browser
        /// navigation) that should receive a 401 rather than an OIDC login redirect. True when
        /// the request carries an <c>Authorization: Bearer</c> header or does not accept HTML.
        /// </summary>
        private static bool IsApiClient(HttpRequest request)
        {
            var authorization = request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return true;

            // A browser top-level navigation sends `Accept: text/html`. XHR/fetch/GraphQL API
            // callers typically send `application/json` (or `*/*`) and never text/html.
            var accept = request.Headers.Accept.ToString();
            if (!string.IsNullOrEmpty(accept)
                && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// When the authenticated principal came from an OIDC provider, projects its raw
        /// provider claims through the registered <see cref="IOidcClaimMapper"/> into the
        /// shared <see cref="Core.Auth.AppIdentity"/> contract and re-issues the cookie
        /// carrying the same normalized claim shape local auth emits. After this runs,
        /// <see cref="BifrostContext"/> reads OIDC and local logins through one identical
        /// path. A principal already in the local-auth shape (no issuer claim, or no
        /// mapper registered for its issuer) is left untouched.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the request may proceed; <c>false</c> when it was rejected
        /// (the response status has been set and the caller must not continue).
        /// </returns>
        private static async Task<bool> NormalizeOidcPrincipalAsync(HttpContext context)
        {
            var registry = context.RequestServices.GetService<OidcClaimMapperRegistry>();
            if (registry == null)
                return true;

            var principal = context.User;
            var mapper = registry.ResolveFor(principal);
            if (mapper == null)
            {
                // No mapper resolved. A principal with no issuer claim is a local-auth
                // login and is read through the local claim path — fine. But a principal
                // that carries an `iss` from a provider this deployment has NOT mapped
                // must be rejected: falling through would read it through the local claim
                // shape, silently dropping its tenant/role claims and running security
                // checks against a stripped identity. Fail closed instead.
                var issuer = principal.FindFirstValue("iss");
                if (!string.IsNullOrWhiteSpace(issuer))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return false;
                }
                return true;
            }

            // Already normalized on a previous request in this cookie session.
            if (principal.FindFirstValue(LocalAuthClaims.Provider) == mapper.Provider)
                return true;

            var identity = mapper.Map(principal);
            var normalized = LocalAuthEndpoint.BuildPrincipal(identity);

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, normalized)
                .ConfigureAwait(false);
            context.User = normalized;
            return true;
        }
    }
}
