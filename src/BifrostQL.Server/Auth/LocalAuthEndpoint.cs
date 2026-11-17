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
            if (options.AdminRole is not null)
                app.Map(options.AdminPath + "/revoke-sessions", branch => branch.Run(HandleRevokeSessionsAsync));
            return app;
        }

        /// <summary>
        /// Builds the cookie-backed <see cref="ClaimsPrincipal"/> for an authenticated
        /// <see cref="AppIdentity"/>. The claims carry exactly the data the
        /// <see cref="BifrostContext"/> needs to reconstruct the same identity contract:
        /// stable id, email, display name, provider, tenant, roles, and the household
        /// provider claim (when the login resolved one).
        /// </summary>
        public static ClaimsPrincipal BuildPrincipal(AppIdentity identity) => BuildPrincipal(identity, validatedAt: null, sessionId: null);

        private static ClaimsPrincipal BuildPrincipal(AppIdentity identity, DateTimeOffset? validatedAt, string? sessionId)
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
            if (sessionId is not null)
                claims.Add(new Claim(LocalAuthClaims.SessionId, sessionId));

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

            var now = Now(context);
            var sourceIp = SourceIp(context);
            var audit = context.RequestServices.GetRequiredService<LocalAuthAudit>();
            if (!result.Succeeded || result.Identity == null || result.Account == null)
            {
                await audit.RecordAsync(now, AuthAuditKind.LoginFailed, AuthAuditOutcome.Failure,
                    result.Account?.UserId, null, sourceIp, FailureReason(result.Account), context.RequestAborted)
                    .ConfigureAwait(false);
                // Same response for missing user, wrong password and disabled account: do not leak which.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var sessionId = await context.RequestServices.GetRequiredService<LocalSessionManager>()
                .IssueAsync(result.Account, sourceIp, now, context.RequestAborted).ConfigureAwait(false);
            var principal = BuildPrincipal(result.Identity, now, sessionId);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal)
                .ConfigureAwait(false);
            await audit.RecordAsync(now, AuthAuditKind.LoginSucceeded, AuthAuditOutcome.Success,
                result.Account.UserId, null, sourceIp, null, context.RequestAborted).ConfigureAwait(false);

            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        private static string FailureReason(LocalAccountState? account) => account switch
        {
            null => AuthAuditReasons.UnknownAccount,
            { Disabled: true } => AuthAuditReasons.AccountDisabled,
            _ => AuthAuditReasons.BadCredentials,
        };

        /// <summary>
        /// Cookie <c>OnValidatePrincipal</c> for local-auth sessions, run on every authenticated
        /// request. The cookie's session id must name a live server-side session: one that is
        /// missing, revoked (logout, administrator, account change), past
        /// <see cref="LocalAuthOptions.SessionLifetime"/> or idle for
        /// <see cref="LocalAuthOptions.SessionIdleTimeout"/> rejects the principal and signs the
        /// cookie out. A cookie without a session id (issued before sessions were server-side) is
        /// rejected the same way. Once <see cref="LocalAuthOptions.SessionRevalidationInterval"/>
        /// has passed since the last check, the user row is re-read: a missing or disabled row, a
        /// changed password, or a changed tenant, role list or household revokes every session of
        /// that user. Otherwise the claims are rebuilt from the row and the cookie re-issued with
        /// its sign-in expiry kept. Principals from other providers (an OIDC re-issue) pass through
        /// untouched.
        /// </summary>
        public static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
        {
            var principal = context.Principal;
            if (principal?.FindFirst(LocalAuthClaims.Provider)?.Value != LocalUserStore.ProviderName)
                return;

            var services = context.HttpContext.RequestServices;
            var options = services.GetRequiredService<LocalAuthOptions>();
            var now = Now(context.HttpContext);
            var sourceIp = SourceIp(context.HttpContext);
            var ct = context.HttpContext.RequestAborted;
            var sessions = services.GetRequiredService<LocalSessionManager>();

            var sessionId = principal.FindFirst(LocalAuthClaims.SessionId)?.Value;
            var session = sessionId is null ? null : await sessions.CheckAsync(sessionId, sourceIp, now, ct).ConfigureAwait(false);
            if (session is null)
            {
                await RejectAsync(context).ConfigureAwait(false);
                return;
            }

            if (long.TryParse(principal.FindFirst(LocalAuthClaims.ValidatedAt)?.Value,
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                    out var validatedSeconds)
                && now - DateTimeOffset.FromUnixTimeSeconds(validatedSeconds) < options.SessionRevalidationInterval)
                return;

            var account = await services.GetRequiredService<LocalUserStore>()
                .FindAccountByIdAsync(session.UserId, ct).ConfigureAwait(false);
            var revokeReason = account switch
            {
                null => LocalSessionRevokeReasons.AccountRemoved,
                { State.Disabled: true } => LocalSessionRevokeReasons.AccountDisabled,
                _ when account.State.CredentialStamp != session.CredentialStamp => LocalSessionRevokeReasons.PasswordChanged,
                _ when PrivilegeChanged(principal, account.Identity) => LocalSessionRevokeReasons.PrivilegeChanged,
                _ => null,
            };
            if (revokeReason is not null)
            {
                if (revokeReason == LocalSessionRevokeReasons.PrivilegeChanged)
                    await services.GetRequiredService<LocalAuthAudit>().RecordAsync(now, AuthAuditKind.PrivilegeChanged,
                        AuthAuditOutcome.Success, session.UserId, null, sourceIp, null, ct).ConfigureAwait(false);
                await sessions.RevokeAllAsync(session.UserId, revokeReason, null, sourceIp, now, ct).ConfigureAwait(false);
                await RejectAsync(context).ConfigureAwait(false);
                return;
            }

            context.ReplacePrincipal(BuildPrincipal(account!.Identity, now, sessionId));
            // A renewal re-issues the cookie expiring at now + (ExpiresUtc - IssuedUtc). Moving
            // IssuedUtc to now makes that the ORIGINAL expiry, so carrying fresh claims never
            // extends the session past SessionLifetime from sign-in.
            context.Properties.IssuedUtc = now;
            context.ShouldRenew = true;
        }

        private static async Task RejectAsync(CookieValidatePrincipalContext context)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        }

        /// <summary>
        /// True when the claims that scope what a session may read or do (tenant, role list,
        /// household) differ between the cookie and the user row. Display name and email changes
        /// only refresh the claims.
        /// </summary>
        private static bool PrivilegeChanged(ClaimsPrincipal principal, AppIdentity current)
        {
            var fresh = BuildPrincipal(current);
            static string? One(ClaimsPrincipal p, string type) => p.FindFirst(type)?.Value;
            static string[] All(ClaimsPrincipal p, string type) =>
                p.FindAll(type).Select(c => c.Value).Order(StringComparer.Ordinal).ToArray();
            return One(principal, LocalAuthClaims.Tenant) != One(fresh, LocalAuthClaims.Tenant)
                || One(principal, LocalAuthClaims.Household) != One(fresh, LocalAuthClaims.Household)
                || !All(principal, ClaimTypes.Role).SequenceEqual(All(fresh, ClaimTypes.Role), StringComparer.Ordinal)
                || !All(principal, LocalAuthClaims.Org).SequenceEqual(All(fresh, LocalAuthClaims.Org), StringComparer.Ordinal);
        }

        private static DateTimeOffset Now(HttpContext context) =>
            (context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

        private static string? SourceIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString();

        private static System.Net.EndPoint? RemoteEndPoint(HttpContext context) =>
            context.Connection.RemoteIpAddress is { } ip ? new System.Net.IPEndPoint(ip, 0) : null;

        private static async Task HandleLogoutAsync(HttpContext context)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            // Revoke the server-side session first: deleting the cookie alone would leave a copied
            // cookie usable until it expired.
            if (context.User.FindFirst(LocalAuthClaims.Provider)?.Value == LocalUserStore.ProviderName
                && context.User.FindFirst(LocalAuthClaims.SessionId)?.Value is { } sessionId)
                await context.RequestServices.GetRequiredService<LocalSessionManager>()
                    .LogoutAsync(sessionId, SourceIp(context), Now(context), context.RequestAborted).ConfigureAwait(false);

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        /// <summary>
        /// <c>POST {AdminPath}/revoke-sessions</c> with <c>{"userId": "..."}</c>: revokes every live
        /// session of that user. Mapped only when <see cref="LocalAuthOptions.AdminRole"/> is set, and
        /// served only to an authenticated caller holding that role. The JSON content type is required
        /// so a cross-site form post cannot reach it.
        /// </summary>
        private static async Task HandleRevokeSessionsAsync(HttpContext context)
        {
            var target = await ReadAdminTargetAsync(context).ConfigureAwait(false);
            if (target is null)
                return;
            await context.RequestServices.GetRequiredService<LocalAuthAdministration>()
                .RevokeAllSessionsAsync(target.Value.UserId, target.Value.ActorId, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        /// <summary>
        /// Shared gate of the administration endpoints: POST, an authenticated caller in
        /// <see cref="LocalAuthOptions.AdminRole"/>, a JSON body naming a user id. Writes the refusal and
        /// returns null when any of that fails.
        /// </summary>
        private static async Task<(string UserId, string ActorId)?> ReadAdminTargetAsync(HttpContext context)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return null;
            }
            var options = context.RequestServices.GetRequiredService<LocalAuthOptions>();
            var actorId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (context.User.Identity?.IsAuthenticated != true || actorId is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return null;
            }
            if (!context.User.IsInRole(options.AdminRole!))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return null;
            }
            if (!context.Request.HasJsonContentType())
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return null;
            }
            LocalAdminRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<LocalAdminRequest>(context.RequestAborted).ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                request = null;
            }
            if (string.IsNullOrWhiteSpace(request?.UserId))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return null;
            }
            return (request.UserId, actorId);
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

        /// <summary>
        /// Claim carrying the random id of the server-side session (<see cref="ILocalAuthStateStore"/>).
        /// The store keeps only its SHA-256.
        /// </summary>
        public const string SessionId = "bifrost:sid";
    }

    /// <summary>JSON body of the local-auth administration endpoints.</summary>
    public sealed record LocalAdminRequest
    {
        /// <summary>The app-user id the action targets.</summary>
        public string? UserId { get; init; }
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
