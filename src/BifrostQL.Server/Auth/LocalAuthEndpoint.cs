using System.Security.Claims;
using BifrostQL.Core.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// Maps the local-user login and logout endpoints. The login endpoint accepts a
    /// JSON credential payload, verifies it server-side through <see cref="LocalUserStore"/>,
    /// and on success issues a cookie-backed <see cref="ClaimsPrincipal"/>. Database
    /// credentials never leave the server: only the resulting session cookie is returned
    /// to the client.
    /// </summary>
    public static class LocalAuthEndpoint
    {
        /// <summary>
        /// Registers the local auth login and logout endpoints on the application pipeline.
        /// Call after authentication middleware so the issued cookie is honored on
        /// subsequent requests.
        /// </summary>
        public static IApplicationBuilder UseBifrostLocalAuth(this IApplicationBuilder app)
        {
            var options = app.ApplicationServices.GetService<LocalAuthOptions>()
                ?? throw new InvalidOperationException(
                    "LocalAuthOptions not registered. Call AddBifrostLocalAuth() during service configuration.");

            app.Map(options.LoginPath, branch => branch.Run(HandleLoginAsync));
            app.Map(options.LogoutPath, branch => branch.Run(HandleLogoutAsync));
            return app;
        }

        /// <summary>
        /// Builds the cookie-backed <see cref="ClaimsPrincipal"/> for an authenticated
        /// <see cref="AppIdentity"/>. The claims carry exactly the data the
        /// <see cref="BifrostContext"/> needs to reconstruct the same identity contract:
        /// stable id, email, display name, provider, tenant, and roles.
        /// </summary>
        public static ClaimsPrincipal BuildPrincipal(AppIdentity identity)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, identity.Id),
                new(LocalAuthClaims.Provider, identity.Provider),
            };

            if (!string.IsNullOrWhiteSpace(identity.Email))
                claims.Add(new Claim(ClaimTypes.Email, identity.Email));
            if (!string.IsNullOrWhiteSpace(identity.DisplayName))
                claims.Add(new Claim(ClaimTypes.Name, identity.DisplayName));
            if (!string.IsNullOrWhiteSpace(identity.TenantId))
                claims.Add(new Claim(LocalAuthClaims.Tenant, identity.TenantId));
            foreach (var orgId in identity.OrgIds)
                claims.Add(new Claim(LocalAuthClaims.Org, orgId));
            foreach (var role in identity.Roles)
                claims.Add(new Claim(ClaimTypes.Role, role));

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            return new ClaimsPrincipal(claimsIdentity);
        }

        private static async Task HandleLoginAsync(HttpContext context)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            var store = context.RequestServices.GetService<LocalUserStore>();
            if (store == null)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            LocalLoginRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<LocalLoginRequest>(context.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                request = null;
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Login) || string.IsNullOrEmpty(request.Password))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var result = await store
                .VerifyCredentialsAsync(request.Login, request.Password, context.RequestAborted)
                .ConfigureAwait(false);

            if (!result.Succeeded || result.Identity == null)
            {
                // Same response for missing user and wrong password: do not leak which.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var principal = BuildPrincipal(result.Identity);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal)
                .ConfigureAwait(false);

            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        private static async Task HandleLogoutAsync(HttpContext context)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
    }

    /// <summary>Claim types used by local auth that have no standard <see cref="ClaimTypes"/> equivalent.</summary>
    public static class LocalAuthClaims
    {
        /// <summary>Claim carrying the authentication provider name (e.g. <c>local</c>).</summary>
        public const string Provider = "bifrost:provider";

        /// <summary>Claim carrying the user's primary tenant identifier.</summary>
        public const string Tenant = "bifrost:tenant";

        /// <summary>Claim carrying an organization/group identifier the user belongs to. Repeated per org.</summary>
        public const string Org = "bifrost:org";
    }

    /// <summary>JSON body accepted by the local auth login endpoint.</summary>
    public sealed record LocalLoginRequest
    {
        /// <summary>The login name (matched against the configured login column).</summary>
        public string? Login { get; init; }

        /// <summary>The plaintext password, verified server-side against the stored hash.</summary>
        public string? Password { get; init; }
    }
}
