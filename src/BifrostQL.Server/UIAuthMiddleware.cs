using System.Security.Claims;
using BifrostQL.Server.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
                    await context.ChallengeAsync("oauth2", new AuthenticationProperties()
                    {
                        RedirectUri = "/"
                    });
                }
                else
                {
                    await NormalizeOidcPrincipalAsync(context);
                    await next.Invoke(context);
                }
            });
            return app;
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
        private static async Task NormalizeOidcPrincipalAsync(HttpContext context)
        {
            var registry = context.RequestServices.GetService<OidcClaimMapperRegistry>();
            if (registry == null)
                return;

            var principal = context.User;
            var mapper = registry.ResolveFor(principal);
            if (mapper == null)
                return;

            // Already normalized on a previous request in this cookie session.
            if (principal.FindFirstValue(LocalAuthClaims.Provider) == mapper.Provider)
                return;

            var identity = mapper.Map(principal);
            var normalized = LocalAuthEndpoint.BuildPrincipal(identity);

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, normalized)
                .ConfigureAwait(false);
            context.User = normalized;
        }
    }
}
