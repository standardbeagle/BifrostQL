using System.Security.Claims;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
            app.Map(options.SessionPath, branch => branch.Run(HandleSessionAsync));
            return app;
        }

        /// <summary>
        /// Builds the cookie-backed <see cref="ClaimsPrincipal"/> for an authenticated
        /// <see cref="AppIdentity"/>. The claims carry exactly the data the
        /// <see cref="BifrostContext"/> needs to reconstruct the same identity contract:
        /// stable id, email, display name, provider, tenant, roles, and the household
        /// provider claim (when the login resolved one).
        /// </summary>
        public static ClaimsPrincipal BuildPrincipal(AppIdentity identity) => BuildPrincipal(identity, validatedAt: null);

        private static ClaimsPrincipal BuildPrincipal(AppIdentity identity, DateTimeOffset? validatedAt)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, identity.Id),
                new(LocalAuthClaims.Provider, identity.Provider),
            };
            if (validatedAt is { } at)
                claims.Add(new Claim(LocalAuthClaims.ValidatedAt,
                    at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));

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

            // The household provider claim resolved from the member row at login
            // is carried through the cookie so BifrostContext can re-surface it.
            if (identity.Claims.TryGetValue(MetadataKeys.Auth.HouseholdClaimKey, out var household)
                && household is not null
                && !string.IsNullOrWhiteSpace(household.ToString()))
                claims.Add(new Claim(LocalAuthClaims.Household, household.ToString()!));

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

            // Fail closed against online guessing: the attempt is counted against its source IP
            // and the login it names BEFORE the hash is verified, and refused when either window
            // is full. Reserving at admission means parallel requests cannot all slip past a
            // check-then-record gap; the account axis bounds guessing spread over many IPs.
            var limiter = context.RequestServices.GetRequiredService<LocalLoginRateLimiter>();
            if (!limiter.TryLogin(ProtocolSourceKey.Of(RemoteEndPoint(context)), request.Login))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
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

            var principal = BuildPrincipal(result.Identity, Now(context));
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal)
                .ConfigureAwait(false);

            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        /// <summary>
        /// Cookie <c>OnValidatePrincipal</c> for local-auth sessions. A session issued by the
        /// login endpoint carries its claims from sign-in, so without this an admin's role
        /// removal, a tenant move or a deleted row would not reach a live cookie until it
        /// expired. Once <see cref="LocalAuthOptions.SessionRevalidationInterval"/> has passed
        /// since the last check, the user row is re-read: a missing row rejects the principal
        /// and signs the cookie out; otherwise the claims are rebuilt from the row and the
        /// cookie re-issued with its sign-in expiry kept. Principals from other providers (an OIDC
        /// re-issue) pass through untouched.
        /// </summary>
        public static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
        {
            var principal = context.Principal;
            if (principal?.FindFirst(LocalAuthClaims.Provider)?.Value != LocalUserStore.ProviderName)
                return;

            var services = context.HttpContext.RequestServices;
            var options = services.GetRequiredService<LocalAuthOptions>();
            var now = Now(context.HttpContext);
            if (long.TryParse(principal.FindFirst(LocalAuthClaims.ValidatedAt)?.Value,
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                    out var validatedSeconds)
                && now - DateTimeOffset.FromUnixTimeSeconds(validatedSeconds) < options.SessionRevalidationInterval)
                return;

            var id = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var identity = id is null
                ? null
                : await services.GetRequiredService<LocalUserStore>()
                    .FindIdentityByIdAsync(id, context.HttpContext.RequestAborted).ConfigureAwait(false);
            if (identity is null)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
                    .ConfigureAwait(false);
                return;
            }

            context.ReplacePrincipal(BuildPrincipal(identity, now));
            // A renewal re-issues the cookie expiring at now + (ExpiresUtc - IssuedUtc). Moving
            // IssuedUtc to now makes that the ORIGINAL expiry, so carrying fresh claims never
            // extends the session past SessionLifetime from sign-in.
            context.Properties.IssuedUtc = now;
            context.ShouldRenew = true;
        }

        private static DateTimeOffset Now(HttpContext context) =>
            (context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

        private static System.Net.EndPoint? RemoteEndPoint(HttpContext context) =>
            context.Connection.RemoteIpAddress is { } ip ? new System.Net.IPEndPoint(ip, 0) : null;

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

        /// <summary>
        /// Returns the current session as the camelCase <see cref="AppIdentity"/> contract
        /// the app-shell SessionProvider reads, reconstructed from the authenticated
        /// <see cref="ClaimsPrincipal"/> via the same <see cref="BifrostContext.BuildAppIdentity"/>
        /// path the GraphQL pipeline uses. Returns 401 when the request carries no
        /// authenticated principal. Only the public AppIdentity fields are written: the
        /// database credentials and the raw cookie claims never reach the client.
        /// </summary>
        internal static async Task HandleSessionAsync(HttpContext context)
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            var principal = context.User;
            if (principal?.Identity == null || !principal.Identity.IsAuthenticated)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            AppIdentity identity;
            try
            {
                identity = BifrostContext.BuildAppIdentity(principal);
            }
            catch (InvalidOperationException ex)
            {
                // An authenticated principal with no subject claim. Refused with the same
                // 403 the HTTP mounts answer for the same condition (protocol-adapter-security
                // invariant 9), never escaped to the host as an unhandled fault (M13); the
                // detail goes to the log only.
                context.RequestServices?.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(LocalAuthEndpoint))
                    .LogWarning(ex, "Session principal could not be projected; refusing the request.");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await context.Response.WriteAsJsonAsync(identity, context.RequestAborted).ConfigureAwait(false);
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

        /// <summary>
        /// Claim carrying the household identifier resolved from the user's member
        /// row. Carried through the cookie so <see cref="BifrostContext.BuildAppIdentity"/>
        /// can re-surface it as the <c>household_id</c> provider claim.
        /// </summary>
        public const string Household = "bifrost:household";

        /// <summary>
        /// Claim carrying when the session's claims were last read from the user row (Unix
        /// seconds). Drives <see cref="LocalAuthOptions.SessionRevalidationInterval"/>.
        /// </summary>
        public const string ValidatedAt = "bifrost:validated";
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
