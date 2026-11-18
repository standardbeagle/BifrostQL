using System.Security.Claims;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
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
            app.Map(options.PasswordChangePath, branch => branch.Run(HandlePasswordChangeAsync));
            if (options.MfaEnabled)
                LocalMfaEndpoint.Map(app, options);
            if (options.AdminRole is not null)
            {
                app.Map(options.AdminPath + "/revoke-sessions", branch => branch.Run(HandleRevokeSessionsAsync));
                app.Map(options.AdminPath + "/unlock", branch => branch.Run(HandleUnlockAsync));
                app.Map(options.AdminPath + "/reset-mfa", branch => branch.Run(HandleResetMfaAsync));
            }
            return app;
        }

        /// <summary>
        /// Builds the cookie-backed <see cref="ClaimsPrincipal"/> for an authenticated
        /// <see cref="AppIdentity"/>. The claims carry exactly the data the
        /// <see cref="BifrostContext"/> needs to reconstruct the same identity contract:
        /// stable id, email, display name, provider, tenant, roles, and the household
        /// provider claim (when the login resolved one).
        /// </summary>
        public static ClaimsPrincipal BuildPrincipal(AppIdentity identity) =>
            BuildPrincipal(identity, validatedAt: null, sessionId: null, amr: null, mfaPending: false);

        private static ClaimsPrincipal BuildPrincipal(
            AppIdentity identity, DateTimeOffset? validatedAt, string? sessionId, string? amr, bool mfaPending)
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
            foreach (var method in (amr ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim(LocalAuthClaims.Amr, method));
            if (mfaPending)
                claims.Add(new Claim(LocalAuthClaims.MfaPending, "true"));

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

            var (request, unreadable) = await ReadJsonBodyAsync<LocalLoginRequest>(context).ConfigureAwait(false);
            if (unreadable)
                return;
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
                await RefuseRateLimitedAsync(context, AuthAuditKind.LoginFailed).ConfigureAwait(false);
                return;
            }

            var result = await store
                .VerifyCredentialsAsync(request.Login, request.Password, context.RequestAborted)
                .ConfigureAwait(false);

            var now = Now(context);
            var sourceIp = SourceIp(context);
            var audit = context.RequestServices.GetRequiredService<LocalAuthAudit>();
            var ct = context.RequestAborted;
            if (await RefuseLockedAsync(context, result.Account, AuthAuditKind.LoginFailed, now, sourceIp).ConfigureAwait(false))
                return;

            if (!result.Succeeded || result.Identity == null || result.Account == null)
            {
                await RecordFailedAttemptAsync(context, result.Account, AuthAuditKind.LoginFailed, now, sourceIp)
                    .ConfigureAwait(false);
                // Same response for missing user, wrong password and disabled account: do not leak which.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var state = context.RequestServices.GetRequiredService<ILocalAuthStateStore>();
            var options = context.RequestServices.GetRequiredService<LocalAuthOptions>();
            // The password is right; from here on, saying what the second factor needs reveals
            // nothing a guesser lacks. A wrong password never reaches this point, so its answer
            // (401, no body) and its work are the same whether or not the account has a second factor.
            var mfa = await state.GetMfaAsync(result.Account.UserId, ct).ConfigureAwait(false);
            var secondFactorOwed = mfa is { Status: LocalMfaStatus.Active };

            // For an enrolled user the failure count is cleared only when the second factor passes
            // (LocalMfaEndpoint): clearing it on the password alone would let someone holding only
            // the password reset the lockout between rounds of code guesses.
            if (!secondFactorOwed
                && await RefuseLockedSinceCheckAsync(context, result.Account.UserId, AuthAuditKind.LoginFailed, now, sourceIp)
                    .ConfigureAwait(false))
                return;

            // Past the lockout check, so a login never rewrites a locked account's row. A rehash keeps
            // the same password: the user's live sessions follow the new stamp instead of reading it
            // as a password change at their next revalidation. It runs before any challenge is issued,
            // so the challenge binds the stamp the user row will carry when the code arrives.
            var account = await store.UpgradePasswordHashAsync(result, request.Login, request.Password, ct).ConfigureAwait(false);
            if (account.CredentialStamp != result.Account.CredentialStamp)
                await state.RebindSessionCredentialStampAsync(
                    account.UserId, result.Account.CredentialStamp, account.CredentialStamp, ct).ConfigureAwait(false);

            if (await PasswordExpiredAsync(context, result.Account.UserId, now).ConfigureAwait(false))
            {
                await audit.RecordAsync(now, AuthAuditKind.LoginFailed, AuthAuditOutcome.Failure,
                    result.Account.UserId, null, sourceIp, AuthAuditReasons.PasswordExpired, ct).ConfigureAwait(false);
                // The password was right, so saying it has expired reveals nothing a guesser lacks.
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "password_expired" }, ct).ConfigureAwait(false);
                return;
            }

            if (secondFactorOwed)
            {
                if (!options.MfaEnabled)
                {
                    // MfaKeyRef was removed after this user enrolled. Fail closed rather than skip the factor.
                    await audit.RecordAsync(now, AuthAuditKind.LoginFailed, AuthAuditOutcome.Failure,
                        account.UserId, null, sourceIp, AuthAuditReasons.MfaUnavailable, ct).ConfigureAwait(false);
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "mfa_unavailable" }, ct).ConfigureAwait(false);
                    return;
                }
                var (ticket, expiresAt) = await context.RequestServices.GetRequiredService<LocalMfaService>()
                    .IssueChallengeAsync(account, now, ct).ConfigureAwait(false);
                await audit.RecordAsync(now, AuthAuditKind.MfaChallengeIssued, AuthAuditOutcome.Success,
                    account.UserId, null, sourceIp, null, ct).ConfigureAwait(false);
                await context.Response.WriteAsJsonAsync(
                    new LocalLoginMfaResponse(LocalLoginMfaResponse.MfaRequired, ticket, expiresAt), ct).ConfigureAwait(false);
                return;
            }

            if (options.MfaEnabled && options.MfaRequiredFor(result.Identity.Roles))
            {
                await SignInSessionAsync(context, result.Identity, account, LocalAuthMethods.Password,
                    mfaPending: true, now, sourceIp).ConfigureAwait(false);
                await audit.RecordAsync(now, AuthAuditKind.LoginSucceeded, AuthAuditOutcome.Success,
                    account.UserId, null, sourceIp, AuthAuditReasons.MfaEnrollmentRequired, ct).ConfigureAwait(false);
                await context.Response.WriteAsJsonAsync(
                    new LocalLoginMfaResponse(LocalLoginMfaResponse.MfaEnrollmentRequired, null, null), ct).ConfigureAwait(false);
                return;
            }

            await SignInSessionAsync(context, result.Identity, account, LocalAuthMethods.Password,
                mfaPending: false, now, sourceIp).ConfigureAwait(false);
            await audit.RecordAsync(now, AuthAuditKind.LoginSucceeded, AuthAuditOutcome.Success,
                result.Account.UserId, null, sourceIp, null, context.RequestAborted).ConfigureAwait(false);

            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        /// <summary>
        /// Issues a server-side session recording <paramref name="amr"/> and signs its cookie in.
        /// The one place a local-auth session cookie is created.
        /// </summary>
        internal static async Task SignInSessionAsync(
            HttpContext context, AppIdentity identity, LocalAccountState account, string amr, bool mfaPending,
            DateTimeOffset now, string? sourceIp)
        {
            var sessionId = await context.RequestServices.GetRequiredService<LocalSessionManager>()
                .IssueAsync(account, sourceIp, now, context.RequestAborted, amr, mfaPending).ConfigureAwait(false);
            var principal = BuildPrincipal(identity, now, sessionId, amr, mfaPending);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal).ConfigureAwait(false);
        }

        /// <summary>
        /// Refuses a verified attempt against a locked account with the wrong-password answer (401),
        /// right password or not, and audits it as <paramref name="kind"/>. Lockout is checked after
        /// verification because only a matched account has an id to look up. A lock that lapsed on its
        /// own is cleared and its end audited, by the one request whose conditional delete removed it:
        /// a failure or lock recorded since the read survives. Returns true when the response is written.
        /// </summary>
        internal static async Task<bool> RefuseLockedAsync(
            HttpContext context, LocalAccountState? account, AuthAuditKind kind, DateTimeOffset now, string? sourceIp)
        {
            if (account is null)
                return false;
            var state = context.RequestServices.GetRequiredService<ILocalAuthStateStore>();
            var audit = context.RequestServices.GetRequiredService<LocalAuthAudit>();
            var ct = context.RequestAborted;
            var lockout = await state.GetLockoutAsync(account.UserId, ct).ConfigureAwait(false);
            if (lockout.IsLockedAt(now))
            {
                await audit.RecordAsync(now, kind, AuthAuditOutcome.Failure,
                    account.UserId, null, sourceIp, AuthAuditReasons.AccountLocked, ct).ConfigureAwait(false);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return true;
            }
            if (lockout.LockedUntil is not null && await state.ClearExpiredLockoutAsync(account.UserId, now, ct).ConfigureAwait(false))
            {
                await audit.RecordAsync(now, AuthAuditKind.AccountUnlocked, AuthAuditOutcome.Success,
                    account.UserId, null, sourceIp, AuthAuditReasons.LockoutExpired, ct).ConfigureAwait(false);
            }
            return false;
        }

        /// <summary>
        /// The success half of the lockout: clears the failure count of an account whose password just
        /// verified, unless a concurrent failure locked it after <see cref="RefuseLockedAsync"/> read the
        /// lockout. Then the attempt is refused like any locked one (401, audited as
        /// <paramref name="kind"/>) and the lock stands. Returns true when the response is written.
        /// </summary>
        internal static async Task<bool> RefuseLockedSinceCheckAsync(
            HttpContext context, string userId, AuthAuditKind kind, DateTimeOffset now, string? sourceIp)
        {
            var ct = context.RequestAborted;
            if (await context.RequestServices.GetRequiredService<ILocalAuthStateStore>()
                    .ClearFailuresUnlessLockedAsync(userId, now, ct).ConfigureAwait(false))
                return false;
            await context.RequestServices.GetRequiredService<LocalAuthAudit>().RecordAsync(now, kind,
                AuthAuditOutcome.Failure, userId, null, sourceIp, AuthAuditReasons.AccountLocked, ct).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return true;
        }

        /// <summary>
        /// Counts a failed credential check toward the lockout of the matched, enabled account, audits
        /// the lock when this failure takes it, and audits the failure as <paramref name="kind"/>. A
        /// wrong second factor passes its own <paramref name="failureReason"/>; it counts toward the
        /// same lockout as a wrong password.
        /// </summary>
        internal static async Task RecordFailedAttemptAsync(
            HttpContext context, LocalAccountState? account, AuthAuditKind kind, DateTimeOffset now, string? sourceIp,
            string? failureReason = null)
        {
            var audit = context.RequestServices.GetRequiredService<LocalAuthAudit>();
            var ct = context.RequestAborted;
            if (account is { Disabled: false })
            {
                var options = context.RequestServices.GetRequiredService<LocalAuthOptions>();
                var failed = await context.RequestServices.GetRequiredService<ILocalAuthStateStore>()
                    .RecordFailedLoginAsync(account.UserId, now, options.LockoutThreshold, options.LockoutEndsAt(now), ct)
                    .ConfigureAwait(false);
                if (failed.LockedNow)
                    await audit.RecordAsync(now, AuthAuditKind.AccountLocked, AuthAuditOutcome.Success,
                        account.UserId, null, sourceIp, failureReason ?? AuthAuditReasons.BadCredentials, ct).ConfigureAwait(false);
            }
            await audit.RecordAsync(now, kind, AuthAuditOutcome.Failure,
                account?.UserId, null, sourceIp, failureReason ?? FailureReason(account), ct).ConfigureAwait(false);
        }

        private static string FailureReason(LocalAccountState? account) => account switch
        {
            null => AuthAuditReasons.UnknownAccount,
            { Disabled: true } => AuthAuditReasons.AccountDisabled,
            _ => AuthAuditReasons.BadCredentials,
        };

        /// <summary>
        /// Cookie <c>OnValidatePrincipal</c> for local-auth sessions, run on every authenticated
        /// request: <see cref="RevalidateSessionAsync"/> decides, this hook signs a rejected cookie out
        /// or re-issues a refreshed one with its sign-in expiry kept. Principals from other providers
        /// (an OIDC re-issue) pass through untouched.
        /// </summary>
        public static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
        {
            var check = await RevalidateSessionAsync(context.Principal, context.HttpContext, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            switch (check.Verdict)
            {
                case LocalSessionVerdict.Rejected:
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
                    return;
                case LocalSessionVerdict.Refreshed:
                    context.ReplacePrincipal(check.Principal!);
                    // A renewal re-issues the cookie expiring at now + (ExpiresUtc - IssuedUtc). Moving
                    // IssuedUtc to now makes that the ORIGINAL expiry, so carrying fresh claims never
                    // extends the session past SessionLifetime from sign-in.
                    context.Properties.IssuedUtc = check.CheckedAt;
                    context.ShouldRenew = true;
                    return;
                case LocalSessionVerdict.EnrollmentOnly:
                    // An enrollment-only session counts as signed in on the enrollment and logout
                    // endpoints alone. Everywhere else the request runs anonymous, whatever is mounted
                    // and in whatever order; the cookie is kept so the user can still finish enrolling.
                    if (!IsEnrollmentOnlyPath(context.HttpContext.RequestServices.GetRequiredService<LocalAuthOptions>(),
                            context.HttpContext.Request.Path))
                    {
                        context.RejectPrincipal();
                        return;
                    }
                    if (check.Principal is { } refreshed)
                    {
                        context.ReplacePrincipal(refreshed);
                        context.Properties.IssuedUtc = check.CheckedAt;
                        context.ShouldRenew = true;
                    }
                    return;
            }
        }

        /// <summary>
        /// The one local-auth session check, shared by every surface that holds a principal past the
        /// start of its request: the cookie hook per HTTP request, the binary WebSocket per message,
        /// the chat stream per tool call. The principal's session id must name a live server-side
        /// session: one that is missing, revoked (logout, administrator, account change), past
        /// <see cref="LocalAuthOptions.SessionLifetime"/> or idle for
        /// <see cref="LocalAuthOptions.SessionIdleTimeout"/> is <see cref="LocalSessionVerdict.Rejected"/>.
        /// A principal without a session id (issued before sessions were server-side) is rejected the
        /// same way. Once <see cref="LocalAuthOptions.SessionRevalidationInterval"/> has passed since
        /// the principal's last check, the user row is re-read: a missing or disabled row, a changed
        /// password, or a changed tenant, role list or household revokes every session of that user
        /// (rejected); otherwise the verdict is <see cref="LocalSessionVerdict.Refreshed"/> with claims
        /// rebuilt from the row, which the caller adopts as its principal. Principals from other
        /// providers are <see cref="LocalSessionVerdict.NotLocal"/>.
        /// </summary>
        internal static async Task<LocalSessionCheck> RevalidateSessionAsync(
            ClaimsPrincipal? principal, HttpContext httpContext, CancellationToken ct)
        {
            if (principal?.FindFirst(LocalAuthClaims.Provider)?.Value != LocalUserStore.ProviderName)
                return new LocalSessionCheck(LocalSessionVerdict.NotLocal, null, default);

            var services = httpContext.RequestServices;
            var options = services.GetRequiredService<LocalAuthOptions>();
            var now = Now(httpContext);
            var sourceIp = SourceIp(httpContext);
            var sessions = services.GetRequiredService<LocalSessionManager>();

            var sessionId = principal.FindFirst(LocalAuthClaims.SessionId)?.Value;
            var session = sessionId is null ? null : await sessions.CheckAsync(sessionId, sourceIp, now, ct).ConfigureAwait(false);
            if (session is null)
                return new LocalSessionCheck(LocalSessionVerdict.Rejected, null, now);

            if (!session.MfaPending && !session.HasSecondFactor && options.MfaEnabled
                && options.MfaRequiredFor(principal.FindAll(ClaimTypes.Role).Select(c => c.Value)))
            {
                // A password-only session of a user who must present a second factor: issued before
                // the requirement applied to them. It ends; the next sign-in asks for the factor.
                await sessions.RevokeAsync(session, LocalSessionRevokeReasons.MfaRequired, actorId: null, sourceIp, now, ct)
                    .ConfigureAwait(false);
                return new LocalSessionCheck(LocalSessionVerdict.Rejected, null, now);
            }

            if (long.TryParse(principal.FindFirst(LocalAuthClaims.ValidatedAt)?.Value,
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                    out var validatedSeconds)
                && now - DateTimeOffset.FromUnixTimeSeconds(validatedSeconds) < options.SessionRevalidationInterval)
                return Admitted(session, refreshed: null, now);

            var account = await services.GetRequiredService<LocalUserStore>()
                .FindAccountByIdAsync(session.UserId, ct).ConfigureAwait(false);
            // A login that rehashes the password moves the user's sessions to the new stamp right
            // after writing the hash. Re-read the session once so that window does not read as a
            // password change.
            if (account is not null && account.State.CredentialStamp != session.CredentialStamp)
                session = await services.GetRequiredService<ILocalAuthStateStore>()
                    .GetSessionAsync(session.SessionHash, ct).ConfigureAwait(false) ?? session;
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
                return new LocalSessionCheck(LocalSessionVerdict.Rejected, null, now);
            }

            return Admitted(session, BuildPrincipal(account!.Identity, now, sessionId, session.Amr, session.MfaPending), now);
        }

        /// <summary>
        /// The verdict for a session that passed every check: <see cref="LocalSessionVerdict.EnrollmentOnly"/>
        /// for an enrollment-only session, else <see cref="LocalSessionVerdict.Live"/> (no
        /// <paramref name="refreshed"/> principal) or <see cref="LocalSessionVerdict.Refreshed"/>.
        /// </summary>
        private static LocalSessionCheck Admitted(LocalSession session, ClaimsPrincipal? refreshed, DateTimeOffset now) =>
            session.MfaPending
                ? new LocalSessionCheck(LocalSessionVerdict.EnrollmentOnly, refreshed, now)
                : new LocalSessionCheck(refreshed is null ? LocalSessionVerdict.Live : LocalSessionVerdict.Refreshed, refreshed, now);

        /// <summary>The paths an enrollment-only session may reach: enroll, confirm, logout. Exact match, any case.</summary>
        private static bool IsEnrollmentOnlyPath(LocalAuthOptions options, PathString path) =>
            path.Equals(options.MfaPath + LocalMfaEndpoint.EnrollSuffix, StringComparison.OrdinalIgnoreCase)
            || path.Equals(options.MfaPath + LocalMfaEndpoint.EnrollConfirmSuffix, StringComparison.OrdinalIgnoreCase)
            || path.Equals(options.LogoutPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Answers 429 for an attempt the rate limiter refused and audits it as a failed
        /// <paramref name="kind"/> (PCI DSS 10.2.1.4). The refusal runs before any account lookup, so
        /// the event names no subject, and it never records the login name typed: a mistyped
        /// password in the login field would otherwise land in the audit log.
        /// </summary>
        private static async Task RefuseRateLimitedAsync(HttpContext context, AuthAuditKind kind)
        {
            await context.RequestServices.GetRequiredService<LocalAuthAudit>().RecordAsync(Now(context), kind,
                AuthAuditOutcome.Failure, null, null, SourceIp(context), AuthAuditReasons.RateLimited,
                context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        }

        /// <summary>
        /// Reads a local-auth endpoint's JSON body. A body that is not JSON (another content type, or
        /// a JSON type with a charset .NET cannot decode) answers 415 and reports
        /// <c>Unreadable</c>, so the caller returns. Malformed JSON reads as a null body, which the
        /// caller's own field check answers with 400.
        /// </summary>
        private static async Task<(T? Body, bool Unreadable)> ReadJsonBodyAsync<T>(HttpContext context) where T : class
        {
            if (!context.Request.HasJsonContentType())
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return (null, true);
            }
            try
            {
                return (await context.Request.ReadFromJsonAsync<T>(context.RequestAborted).ConfigureAwait(false), false);
            }
            catch (System.Text.Json.JsonException)
            {
                return (null, false);
            }
            catch (InvalidOperationException)
            {
                // ReadFromJsonAsync's refusal of an undecodable charset.
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return (null, true);
            }
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

        internal static DateTimeOffset Now(HttpContext context) =>
            (context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

        internal static string? SourceIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString();

        internal static System.Net.EndPoint? RemoteEndPoint(HttpContext context) =>
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
        /// True when <see cref="LocalAuthOptions.MaxPasswordAge"/> is set and the user's newest
        /// recorded password change is older than it, or no change is recorded.
        /// </summary>
        private static async Task<bool> PasswordExpiredAsync(HttpContext context, string userId, DateTimeOffset now)
        {
            if (context.RequestServices.GetRequiredService<LocalAuthOptions>().MaxPasswordAge is not { } maxAge)
                return false;
            var newest = await context.RequestServices.GetRequiredService<ILocalAuthStateStore>()
                .GetPasswordHistoryAsync(userId, 1, context.RequestAborted).ConfigureAwait(false);
            return newest.Count == 0 || now - newest[0].ChangedAt >= maxAge;
        }

        /// <summary>
        /// <c>POST {PasswordChangePath}</c> with <c>{"login", "currentPassword", "newPassword"}</c>.
        /// The current password is checked like a login (rate windows, lockout, audit), so the
        /// endpoint is no side door for guessing; an expired password is accepted here. The new
        /// password must satisfy <see cref="LocalAuthOptions.MinPasswordLength"/>,
        /// <see cref="LocalAuthOptions.MaxPasswordLength"/>, <see cref="LocalAuthOptions.PasswordRequiresLetterAndDigit"/> and differ from the current password and the
        /// <see cref="LocalAuthOptions.PasswordHistoryCount"/> passwords before it, else 400 with the broken
        /// rule. An account with a second factor also sends <c>code</c> or <c>recoveryCode</c>, checked
        /// before the failure count clears. On success every session of the user is revoked and the
        /// caller's cookie cleared; the user signs in again with the new password.
        /// </summary>
        private static async Task HandlePasswordChangeAsync(HttpContext context)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }
            var store = context.RequestServices.GetRequiredService<LocalUserStore>();
            var (request, unreadable) = await ReadJsonBodyAsync<LocalPasswordChangeRequest>(context).ConfigureAwait(false);
            if (unreadable)
                return;
            if (request == null || string.IsNullOrWhiteSpace(request.Login)
                || string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var limiter = context.RequestServices.GetRequiredService<LocalLoginRateLimiter>();
            if (!limiter.TryLogin(ProtocolSourceKey.Of(RemoteEndPoint(context)), request.Login))
            {
                await RefuseRateLimitedAsync(context, AuthAuditKind.PasswordChangeRejected).ConfigureAwait(false);
                return;
            }

            var ct = context.RequestAborted;
            var result = await store.VerifyCredentialsAsync(request.Login, request.CurrentPassword, ct).ConfigureAwait(false);
            var now = Now(context);
            var sourceIp = SourceIp(context);
            if (await RefuseLockedAsync(context, result.Account, AuthAuditKind.PasswordChangeRejected, now, sourceIp)
                    .ConfigureAwait(false))
                return;
            if (!result.Succeeded || result.Account == null)
            {
                await RecordFailedAttemptAsync(context, result.Account, AuthAuditKind.PasswordChangeRejected, now, sourceIp)
                    .ConfigureAwait(false);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var userId = result.Account.UserId;
            var options = context.RequestServices.GetRequiredService<LocalAuthOptions>();
            var state = context.RequestServices.GetRequiredService<ILocalAuthStateStore>();
            var audit = context.RequestServices.GetRequiredService<LocalAuthAudit>();
            // The failure count clears only once every factor the account has passed: the password
            // alone must not reset the lockout an attacker is guessing codes against.
            if (!await PasswordChangeFactorPassesAsync(context, request, result.Account, options, state, now, sourceIp)
                    .ConfigureAwait(false))
                return;
            if (await RefuseLockedSinceCheckAsync(context, userId, AuthAuditKind.PasswordChangeRejected, now, sourceIp)
                    .ConfigureAwait(false))
                return;

            var violation = await PasswordPolicyViolationAsync(
                options, state, store, request.Login, userId, request.CurrentPassword, request.NewPassword, ct).ConfigureAwait(false);
            if (violation is not null)
            {
                await audit.RecordAsync(now, AuthAuditKind.PasswordChangeRejected, AuthAuditOutcome.Failure,
                    userId, null, sourceIp, AuthAuditReasons.PasswordPolicy, ct).ConfigureAwait(false);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "password_policy", rule = violation }, ct).ConfigureAwait(false);
                return;
            }

            var hash = await store.SetPasswordAsync(userId, request.Login, request.NewPassword, ct).ConfigureAwait(false);
            // The newest entry is the password just set. It also gives MaxPasswordAge a change date
            // when PasswordHistoryCount is 0.
            await state.AppendPasswordHistoryAsync(userId, hash, now, options.PasswordHistoryEntriesKept, ct)
                .ConfigureAwait(false);
            await audit.RecordAsync(now, AuthAuditKind.PasswordChanged, AuthAuditOutcome.Success,
                userId, null, sourceIp, null, ct).ConfigureAwait(false);
            await context.RequestServices.GetRequiredService<LocalSessionManager>()
                .RevokeAllAsync(userId, LocalSessionRevokeReasons.PasswordChanged, null, sourceIp, now, ct).ConfigureAwait(false);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        /// <summary>
        /// For an account with a second factor, the password change also needs a current code or
        /// recovery code (spent like any other); else someone holding only the password could replace
        /// it and lock the owner out. Answers 401 for a missing, wrong or replayed factor (counted
        /// toward the lockout) and 403 <c>mfa_unavailable</c> when MFA is no longer configured. True
        /// when the change may go on.
        /// </summary>
        private static async Task<bool> PasswordChangeFactorPassesAsync(
            HttpContext context, LocalPasswordChangeRequest request, LocalAccountState account, LocalAuthOptions options,
            ILocalAuthStateStore state, DateTimeOffset now, string? sourceIp)
        {
            var ct = context.RequestAborted;
            var mfa = await state.GetMfaAsync(account.UserId, ct).ConfigureAwait(false);
            if (mfa is not { Status: LocalMfaStatus.Active })
                return true;
            if (!options.MfaEnabled)
            {
                await context.RequestServices.GetRequiredService<LocalAuthAudit>().RecordAsync(now,
                    AuthAuditKind.PasswordChangeRejected, AuthAuditOutcome.Failure, account.UserId, null, sourceIp,
                    AuthAuditReasons.MfaUnavailable, ct).ConfigureAwait(false);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "mfa_unavailable" }, ct).ConfigureAwait(false);
                return false;
            }
            var code = string.IsNullOrEmpty(request.Code) ? null : request.Code;
            var recoveryCode = string.IsNullOrEmpty(request.RecoveryCode) ? null : request.RecoveryCode;
            var verification = (code is null) == (recoveryCode is null)
                ? LocalMfaVerification.Rejected(LocalMfaVerdict.Invalid)
                : await context.RequestServices.GetRequiredService<LocalMfaService>().VerifyAsync(
                    account.UserId, LocalMfaStatus.Active, code, recoveryCode, now, onAccepted: null, ct).ConfigureAwait(false);
            if (verification.Verdict != LocalMfaVerdict.Accepted)
            {
                await RecordFailedAttemptAsync(context, account, AuthAuditKind.MfaFailed, now, sourceIp,
                    verification.Verdict == LocalMfaVerdict.Replayed ? AuthAuditReasons.MfaCodeReplayed : AuthAuditReasons.MfaCodeInvalid)
                    .ConfigureAwait(false);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return false;
            }
            var audit = context.RequestServices.GetRequiredService<LocalAuthAudit>();
            await audit.RecordAsync(now, AuthAuditKind.MfaSucceeded, AuthAuditOutcome.Success, account.UserId, null, sourceIp,
                verification.Method == LocalAuthMethods.RecoveryCode ? AuthAuditReasons.RecoveryCode : AuthAuditReasons.Otp, ct)
                .ConfigureAwait(false);
            if (verification.Method == LocalAuthMethods.RecoveryCode)
                await audit.RecordAsync(now, AuthAuditKind.MfaRecoveryCodeUsed, AuthAuditOutcome.Success, account.UserId, null,
                    sourceIp, null, ct).ConfigureAwait(false);
            return true;
        }

        /// <summary>The broken password rule (<c>min_length</c>, <c>max_length</c>, <c>letter_and_digit</c>, <c>reused</c>), or null.</summary>
        private static async Task<string?> PasswordPolicyViolationAsync(
            LocalAuthOptions options, ILocalAuthStateStore state, LocalUserStore store,
            string login, string userId, string currentPassword, string newPassword, CancellationToken ct)
        {
            var length = newPassword.EnumerateRunes().Count();
            if (length < options.MinPasswordLength)
                return "min_length";
            if (length > options.MaxPasswordLength)
                return "max_length";
            if (options.PasswordRequiresLetterAndDigit
                && !(newPassword.EnumerateRunes().Any(System.Text.Rune.IsLetter) && newPassword.EnumerateRunes().Any(System.Text.Rune.IsDigit)))
                return "letter_and_digit";
            if (string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
                return "reused";
            if (options.PasswordHistoryCount == 0)
                return null;
            // The history's newest entry is the current password, so the previous N are the next N.
            foreach (var entry in await state.GetPasswordHistoryAsync(userId, options.PasswordHistoryEntriesKept, ct).ConfigureAwait(false))
            {
                if (store.PasswordMatchesHash(login, entry.PasswordHash, newPassword))
                    return "reused";
            }
            return null;
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
        /// <c>POST {AdminPath}/unlock</c> with <c>{"userId": "..."}</c>: ends a lockout and clears the
        /// failure count. Same gate as <c>revoke-sessions</c>.
        /// </summary>
        private static async Task HandleUnlockAsync(HttpContext context)
        {
            var target = await ReadAdminTargetAsync(context).ConfigureAwait(false);
            if (target is null)
                return;
            await context.RequestServices.GetRequiredService<LocalAuthAdministration>()
                .UnlockAccountAsync(target.Value.UserId, target.Value.ActorId, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        /// <summary>
        /// <c>POST {AdminPath}/reset-mfa</c> with <c>{"userId": "..."}</c>: removes the user's second
        /// factor and revokes every session of the user, audited with the administrator as actor. Same
        /// gate as <c>revoke-sessions</c>.
        /// </summary>
        private static async Task HandleResetMfaAsync(HttpContext context)
        {
            var target = await ReadAdminTargetAsync(context).ConfigureAwait(false);
            if (target is null)
                return;
            await context.RequestServices.GetRequiredService<LocalAuthAdministration>()
                .ResetMfaAsync(target.Value.UserId, target.Value.ActorId, context.RequestAborted).ConfigureAwait(false);
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
            var (request, unreadable) = await ReadJsonBodyAsync<LocalAdminRequest>(context).ConfigureAwait(false);
            if (unreadable)
                return null;
            // The id lands in the audit row's subject column; one longer than that column is refused
            // here, before the action runs, not by the audit write after it.
            if (string.IsNullOrWhiteSpace(request?.UserId) || request.UserId.Length > SqlColumnDefinition.KeyTextMaxLength)
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

        /// <summary>
        /// RFC 8176 authentication-method reference, repeated per method (<c>pwd</c>, <c>otp</c>,
        /// <c>recovery</c>). Mirrors the server-side session's <see cref="LocalSession.Amr"/>.
        /// </summary>
        public const string Amr = "amr";

        /// <summary>
        /// Present (value <c>true</c>) on an enrollment-only session: the user must enroll a second
        /// factor and may reach only the enrollment and logout endpoints. The server-side session's
        /// <see cref="LocalSession.MfaPending"/> is what is enforced; the claim tells the client.
        /// </summary>
        public const string MfaPending = "bifrost:mfa-pending";
    }

    /// <summary>
    /// 200 body of <c>POST /auth/login</c> when the password verified but a second factor is still
    /// owed. <c>mfa-required</c> carries the challenge ticket to send with the code to
    /// <c>{MfaPath}/verify</c>; no session cookie is set. <c>mfa-enrollment-required</c> comes with an
    /// enrollment-only session cookie.
    /// </summary>
    public sealed record LocalLoginMfaResponse(string Status, string? Challenge, DateTimeOffset? ExpiresAt)
    {
        public const string MfaRequired = "mfa-required";
        public const string MfaEnrollmentRequired = "mfa-enrollment-required";
    }

    /// <summary>JSON body of the local-auth password-change endpoint.</summary>
    public sealed record LocalPasswordChangeRequest
    {
        /// <summary>The login (email) of the account.</summary>
        public string? Login { get; init; }

        /// <summary>The password in force now, expired or not.</summary>
        public string? CurrentPassword { get; init; }

        /// <summary>The password to set.</summary>
        public string? NewPassword { get; init; }

        /// <summary>
        /// The current authenticator code. Required, or <see cref="RecoveryCode"/>, when the account
        /// has a second factor: the password alone cannot replace the password.
        /// </summary>
        public string? Code { get; init; }

        /// <summary>A recovery code, instead of <see cref="Code"/>.</summary>
        public string? RecoveryCode { get; init; }
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
