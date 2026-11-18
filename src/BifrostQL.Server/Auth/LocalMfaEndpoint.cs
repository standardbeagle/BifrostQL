using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// The TOTP second-factor endpoints of local auth, under <see cref="LocalAuthOptions.MfaPath"/>.
    /// Mapped by <c>UseBifrostLocalAuth</c> when <see cref="LocalAuthOptions.MfaKeyRef"/> is set.
    /// Every one is a POST with a JSON body (a cross-site form post cannot reach them), and every
    /// presented code is counted by <see cref="LocalMfaRateLimiter"/> before it is checked, refused
    /// while the account is locked, counted toward the lockout when wrong, and audited.
    ///
    /// <list type="bullet">
    /// <item><c>/verify</c> <c>{challenge, code | recoveryCode}</c>: login step two. Spends the
    /// challenge and issues the session (amr <c>pwd otp</c> or <c>pwd recovery</c>).</item>
    /// <item><c>/enroll</c> <c>{}</c>: signed in (an enrollment-only session will do). Issues a secret
    /// and its <c>otpauth://</c> URI as a pending enrollment.</item>
    /// <item><c>/enroll/confirm</c> <c>{code}</c>: activates the enrollment, returns ten recovery codes,
    /// revokes every session and signs the caller in again (amr <c>pwd otp</c>).</item>
    /// <item><c>/disable</c> <c>{code | recoveryCode}</c>: full session. Removes the second factor and
    /// revokes every session, the caller's included.</item>
    /// <item><c>/recovery-codes</c> <c>{code | recoveryCode}</c>: full session. Replaces the recovery
    /// codes, revokes every session and signs the caller in again.</item>
    /// </list>
    /// </summary>
    internal static class LocalMfaEndpoint
    {
        public const string VerifySuffix = "/verify";
        public const string EnrollSuffix = "/enroll";
        public const string EnrollConfirmSuffix = "/enroll/confirm";
        public const string DisableSuffix = "/disable";
        public const string RecoveryCodesSuffix = "/recovery-codes";

        public static void Map(IApplicationBuilder app, LocalAuthOptions options)
        {
            // Map matches path prefixes, so the longer path goes first.
            app.Map(options.MfaPath + EnrollConfirmSuffix, branch => branch.Run(HandleEnrollConfirmAsync));
            app.Map(options.MfaPath + EnrollSuffix, branch => branch.Run(HandleEnrollAsync));
            app.Map(options.MfaPath + VerifySuffix, branch => branch.Run(HandleVerifyAsync));
            app.Map(options.MfaPath + DisableSuffix, branch => branch.Run(HandleDisableAsync));
            app.Map(options.MfaPath + RecoveryCodesSuffix, branch => branch.Run(HandleRegenerateAsync));
        }

        private static async Task<T?> ReadJsonAsync<T>(HttpContext context) where T : class
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return null;
            }
            if (!context.Request.HasJsonContentType())
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return null;
            }
            try
            {
                var body = await context.Request.ReadFromJsonAsync<T>(context.RequestAborted).ConfigureAwait(false);
                if (body is null)
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return body;
            }
            catch (System.Text.Json.JsonException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return null;
            }
        }

        /// <summary>Exactly one of code and recovery code, or null after writing 400.</summary>
        private static (string? Code, string? RecoveryCode)? OneFactor(HttpContext context, string? code, string? recoveryCode)
        {
            if (string.IsNullOrEmpty(code) == string.IsNullOrEmpty(recoveryCode))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return null;
            }
            return (string.IsNullOrEmpty(code) ? null : code, string.IsNullOrEmpty(recoveryCode) ? null : recoveryCode);
        }

        private static bool Admit(HttpContext context, string account)
        {
            if (context.RequestServices.GetRequiredService<LocalMfaRateLimiter>()
                .TryAttempt(ProtocolSourceKey.Of(LocalAuthEndpoint.RemoteEndPoint(context)), account))
                return true;
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return false;
        }

        private static string Reason(LocalMfaVerdict verdict) =>
            verdict == LocalMfaVerdict.Replayed ? AuthAuditReasons.MfaCodeReplayed : AuthAuditReasons.MfaCodeInvalid;

        private static string ReasonFor(string method) =>
            method == LocalAuthMethods.RecoveryCode ? AuthAuditReasons.RecoveryCode : AuthAuditReasons.Otp;

        /// <summary>
        /// Audits an accepted factor as <see cref="AuthAuditKind.MfaSucceeded"/>, plus
        /// <see cref="AuthAuditKind.MfaRecoveryCodeUsed"/> when it was a recovery code.
        /// </summary>
        private static async Task AuditAcceptedAsync(
            LocalAuthAudit audit, string userId, string method, DateTimeOffset now, string? sourceIp, CancellationToken ct)
        {
            await audit.RecordAsync(now, AuthAuditKind.MfaSucceeded, AuthAuditOutcome.Success,
                userId, null, sourceIp, ReasonFor(method), ct).ConfigureAwait(false);
            if (method == LocalAuthMethods.RecoveryCode)
                await audit.RecordAsync(now, AuthAuditKind.MfaRecoveryCodeUsed, AuthAuditOutcome.Success,
                    userId, null, sourceIp, null, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// <c>POST {MfaPath}/verify</c>: login step two. Answers 401 with
        /// <c>mfa_challenge_invalid</c> for a missing, expired, spent or voided ticket (start the login
        /// again), and a bare 401 for a wrong or replayed code and for a locked account, alike.
        /// </summary>
        private static async Task HandleVerifyAsync(HttpContext context)
        {
            var request = await ReadJsonAsync<LocalMfaVerifyRequest>(context).ConfigureAwait(false);
            if (request is null)
                return;
            if (string.IsNullOrEmpty(request.Challenge) || OneFactor(context, request.Code, request.RecoveryCode) is not { } factor)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var services = context.RequestServices;
            var state = services.GetRequiredService<ILocalAuthStateStore>();
            var mfa = services.GetRequiredService<LocalMfaService>();
            var audit = services.GetRequiredService<LocalAuthAudit>();
            var ct = context.RequestAborted;
            var now = LocalAuthEndpoint.Now(context);
            var sourceIp = LocalAuthEndpoint.SourceIp(context);

            var challengeHash = LocalSessionId.Hash(request.Challenge);
            var challenge = await state.GetMfaChallengeAsync(challengeHash, ct).ConfigureAwait(false);
            if (!Admit(context, challenge?.UserId ?? challengeHash))
                return;

            var account = challenge is null || !mfa.IsUsable(challenge, now)
                ? null
                : await services.GetRequiredService<LocalUserStore>().FindAccountByIdAsync(challenge.UserId, ct).ConfigureAwait(false);
            // A password change since the ticket was issued, or a disabled or deleted account, voids it.
            if (challenge is null || account is null || account.State.Disabled
                || account.State.CredentialStamp != challenge.CredentialStamp)
            {
                if (challenge is not null && challenge.ConsumedAt is null)
                    await mfa.SpendChallengeAsync(challenge, now, ct).ConfigureAwait(false);
                await audit.RecordAsync(now, AuthAuditKind.MfaFailed, AuthAuditOutcome.Failure,
                    challenge?.UserId, null, sourceIp, AuthAuditReasons.MfaChallengeInvalid, ct).ConfigureAwait(false);
                await WriteChallengeInvalidAsync(context).ConfigureAwait(false);
                return;
            }

            if (await LocalAuthEndpoint.RefuseLockedAsync(context, account.State, AuthAuditKind.MfaFailed, now, sourceIp)
                    .ConfigureAwait(false))
                return;

            var verification = await mfa.VerifyAsync(account.State.UserId, LocalMfaStatus.Active, factor.Code,
                factor.RecoveryCode, now, onAccepted: null, ct).ConfigureAwait(false);
            if (verification.Verdict != LocalMfaVerdict.Accepted)
            {
                await mfa.RecordChallengeFailureAsync(challengeHash, now, ct).ConfigureAwait(false);
                await LocalAuthEndpoint.RecordFailedAttemptAsync(context, account.State, AuthAuditKind.MfaFailed, now, sourceIp,
                    Reason(verification.Verdict)).ConfigureAwait(false);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // One ticket, one session: the swap fails for every request but the first.
            if (!await mfa.SpendChallengeAsync(challenge, now, ct).ConfigureAwait(false))
            {
                await audit.RecordAsync(now, AuthAuditKind.MfaFailed, AuthAuditOutcome.Failure,
                    challenge.UserId, null, sourceIp, AuthAuditReasons.MfaChallengeInvalid, ct).ConfigureAwait(false);
                await WriteChallengeInvalidAsync(context).ConfigureAwait(false);
                return;
            }

            // The second factor passed: the failure count clears, unless a concurrent failure locked
            // the account since the check above. Then the lock stands and this attempt is refused.
            if (await LocalAuthEndpoint.RefuseLockedSinceCheckAsync(context, account.State.UserId, AuthAuditKind.MfaFailed, now, sourceIp)
                    .ConfigureAwait(false))
                return;
            var amr = LocalAuthMethods.Password + " " + verification.Method;
            await LocalAuthEndpoint.SignInSessionAsync(context, account.Identity, account.State, amr, mfaPending: false, now, sourceIp)
                .ConfigureAwait(false);
            await AuditAcceptedAsync(audit, account.State.UserId, verification.Method!, now, sourceIp, ct).ConfigureAwait(false);
            await audit.RecordAsync(now, AuthAuditKind.LoginSucceeded, AuthAuditOutcome.Success,
                account.State.UserId, null, sourceIp, ReasonFor(verification.Method!), ct).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        private static async Task WriteChallengeInvalidAsync(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "mfa_challenge_invalid" }, context.RequestAborted).ConfigureAwait(false);
        }

        /// <summary>The signed-in local user the request carries, re-read from the user row; null after writing 401.</summary>
        private static async Task<(LocalAccount Account, bool Pending)?> SignedInAsync(HttpContext context)
        {
            var user = context.User;
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (user.Identity?.IsAuthenticated != true || userId is null
                || user.FindFirst(LocalAuthClaims.Provider)?.Value != LocalUserStore.ProviderName)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return null;
            }
            var account = await context.RequestServices.GetRequiredService<LocalUserStore>()
                .FindAccountByIdAsync(userId, context.RequestAborted).ConfigureAwait(false);
            if (account is null || account.State.Disabled)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return null;
            }
            return (account, user.FindFirst(LocalAuthClaims.MfaPending) is not null);
        }

        /// <summary>
        /// Refuses a code from a signed-in user while their account is locked: 403
        /// <c>account_locked</c> (they are signed in, so the lock is no secret). True when written.
        /// </summary>
        private static async Task<bool> RefuseLockedAsync(HttpContext context, string userId, DateTimeOffset now, string? sourceIp)
        {
            var lockout = await context.RequestServices.GetRequiredService<ILocalAuthStateStore>()
                .GetLockoutAsync(userId, context.RequestAborted).ConfigureAwait(false);
            if (!lockout.IsLockedAt(now))
                return false;
            await context.RequestServices.GetRequiredService<LocalAuthAudit>().RecordAsync(now, AuthAuditKind.MfaFailed,
                AuthAuditOutcome.Failure, userId, null, sourceIp, AuthAuditReasons.AccountLocked, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "account_locked" }, context.RequestAborted).ConfigureAwait(false);
            return true;
        }

        /// <summary>A wrong code from a signed-in user: counted toward lockout, audited, 400 <c>mfa_code_invalid</c>.</summary>
        private static async Task RefuseCodeAsync(
            HttpContext context, LocalAccount account, LocalMfaVerdict verdict, DateTimeOffset now, string? sourceIp)
        {
            await LocalAuthEndpoint.RecordFailedAttemptAsync(context, account.State, AuthAuditKind.MfaFailed, now, sourceIp,
                Reason(verdict)).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "mfa_code_invalid" }, context.RequestAborted).ConfigureAwait(false);
        }

        /// <summary>
        /// Revokes every session of the user (other devices included) and signs the caller in again
        /// with <paramref name="amr"/>: the session that changed the second factor continues on a
        /// fresh id, every other one ends.
        /// </summary>
        private static async Task ReissueAsync(HttpContext context, LocalAccount account, string amr, DateTimeOffset now, string? sourceIp)
        {
            await context.RequestServices.GetRequiredService<LocalSessionManager>()
                .RevokeAllAsync(account.State.UserId, LocalSessionRevokeReasons.MfaChanged, null, sourceIp, now, context.RequestAborted)
                .ConfigureAwait(false);
            await LocalAuthEndpoint.SignInSessionAsync(context, account.Identity, account.State, amr, mfaPending: false, now, sourceIp)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// <c>POST {MfaPath}/enroll</c> <c>{}</c>: issues a TOTP secret as a pending enrollment that
        /// lapses after <see cref="LocalAuthOptions.MfaEnrollmentTimeout"/>. 409 <c>mfa_already_enabled</c>
        /// when a second factor is in force. The response carries the base32 secret and the
        /// <c>otpauth://</c> URI for the user to scan or type; nothing else ever returns the secret.
        /// </summary>
        private static async Task HandleEnrollAsync(HttpContext context)
        {
            if (await ReadJsonAsync<LocalMfaEmptyRequest>(context).ConfigureAwait(false) is null)
                return;
            if (await SignedInAsync(context).ConfigureAwait(false) is not { } signedIn)
                return;
            var now = LocalAuthEndpoint.Now(context);
            var ct = context.RequestAborted;
            var userId = signedIn.Account.State.UserId;
            var offer = await context.RequestServices.GetRequiredService<LocalMfaService>()
                .StartEnrollmentAsync(userId, signedIn.Account.Identity.Email ?? userId, now, ct).ConfigureAwait(false);
            if (offer is null)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = "mfa_already_enabled" }, ct).ConfigureAwait(false);
                return;
            }
            await context.RequestServices.GetRequiredService<LocalAuthAudit>().RecordAsync(now, AuthAuditKind.MfaEnrollmentStarted,
                AuthAuditOutcome.Success, userId, null, LocalAuthEndpoint.SourceIp(context), null, ct).ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new LocalMfaEnrollmentResponse(
                offer.Secret, offer.OtpAuthUri, offer.ExpiresAt, offer.State.Algorithm.ToString().ToUpperInvariant(),
                offer.State.Digits, offer.State.PeriodSeconds), ct).ConfigureAwait(false);
        }

        /// <summary>
        /// <c>POST {MfaPath}/enroll/confirm</c> <c>{code}</c>: activates a pending enrollment with its
        /// first code and returns the ten recovery codes, shown this once. 400
        /// <c>mfa_enrollment_expired</c> when no unexpired enrollment is pending.
        /// </summary>
        private static async Task HandleEnrollConfirmAsync(HttpContext context)
        {
            var request = await ReadJsonAsync<LocalMfaCodeRequest>(context).ConfigureAwait(false);
            if (request is null)
                return;
            if (string.IsNullOrEmpty(request.Code))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (await SignedInAsync(context).ConfigureAwait(false) is not { } signedIn)
                return;
            var account = signedIn.Account;
            var userId = account.State.UserId;
            if (!Admit(context, userId))
                return;
            var now = LocalAuthEndpoint.Now(context);
            var sourceIp = LocalAuthEndpoint.SourceIp(context);
            var ct = context.RequestAborted;
            if (await RefuseLockedAsync(context, userId, now, sourceIp).ConfigureAwait(false))
                return;

            var audit = context.RequestServices.GetRequiredService<LocalAuthAudit>();
            var pending = await context.RequestServices.GetRequiredService<ILocalAuthStateStore>().GetMfaAsync(userId, ct).ConfigureAwait(false);
            if (pending is not { Status: LocalMfaStatus.Pending } || pending.PendingExpiresAt <= now)
            {
                await audit.RecordAsync(now, AuthAuditKind.MfaEnabled, AuthAuditOutcome.Failure,
                    userId, null, sourceIp, AuthAuditReasons.MfaEnrollmentExpired, ct).ConfigureAwait(false);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "mfa_enrollment_expired" }, ct).ConfigureAwait(false);
                return;
            }

            var (codes, hashes) = LocalMfaService.NewRecoveryCodes();
            var verification = await context.RequestServices.GetRequiredService<LocalMfaService>().VerifyAsync(
                userId, LocalMfaStatus.Pending, request.Code, recoveryCode: null, now,
                s => s.PendingExpiresAt <= now
                    ? throw new InvalidOperationException("unreachable: expiry checked above")
                    : s with { Status = LocalMfaStatus.Active, EnabledAt = now, PendingExpiresAt = null, RecoveryCodeHashes = hashes },
                ct).ConfigureAwait(false);
            if (verification.Verdict != LocalMfaVerdict.Accepted)
            {
                await RefuseCodeAsync(context, account, verification.Verdict, now, sourceIp).ConfigureAwait(false);
                return;
            }

            if (await LocalAuthEndpoint.RefuseLockedSinceCheckAsync(context, userId, AuthAuditKind.MfaFailed, now, sourceIp)
                    .ConfigureAwait(false))
                return;
            await audit.RecordAsync(now, AuthAuditKind.MfaEnabled, AuthAuditOutcome.Success, userId, null, sourceIp, null, ct)
                .ConfigureAwait(false);
            await ReissueAsync(context, account, LocalAuthMethods.Password + " " + LocalAuthMethods.Otp, now, sourceIp).ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new LocalMfaRecoveryCodesResponse(codes), ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Shared front of disable and regenerate: a full (not enrollment-only) session, an active
        /// second factor, the rate windows, the lockout, then the presented factor. Returns the account
        /// and the accepted verification, or null after writing the refusal.
        /// </summary>
        private static async Task<(LocalAccount Account, LocalMfaVerification Verification)?> PresentFactorAsync(
            HttpContext context, Func<LocalMfaState, LocalMfaState>? onAccepted)
        {
            var request = await ReadJsonAsync<LocalMfaFactorRequest>(context).ConfigureAwait(false);
            if (request is null)
                return null;
            if (OneFactor(context, request.Code, request.RecoveryCode) is not { } factor)
                return null;
            if (await SignedInAsync(context).ConfigureAwait(false) is not { } signedIn)
                return null;
            var account = signedIn.Account;
            var userId = account.State.UserId;
            var ct = context.RequestAborted;
            if (signedIn.Pending)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return null;
            }
            var current = await context.RequestServices.GetRequiredService<ILocalAuthStateStore>().GetMfaAsync(userId, ct).ConfigureAwait(false);
            if (current is not { Status: LocalMfaStatus.Active })
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = "mfa_not_enabled" }, ct).ConfigureAwait(false);
                return null;
            }
            if (!Admit(context, userId))
                return null;
            var now = LocalAuthEndpoint.Now(context);
            var sourceIp = LocalAuthEndpoint.SourceIp(context);
            if (await RefuseLockedAsync(context, userId, now, sourceIp).ConfigureAwait(false))
                return null;

            var verification = await context.RequestServices.GetRequiredService<LocalMfaService>().VerifyAsync(
                userId, LocalMfaStatus.Active, factor.Code, factor.RecoveryCode, now, onAccepted, ct).ConfigureAwait(false);
            if (verification.Verdict != LocalMfaVerdict.Accepted)
            {
                await RefuseCodeAsync(context, account, verification.Verdict, now, sourceIp).ConfigureAwait(false);
                return null;
            }
            if (await LocalAuthEndpoint.RefuseLockedSinceCheckAsync(context, userId, AuthAuditKind.MfaFailed, now, sourceIp)
                    .ConfigureAwait(false))
                return null;
            await AuditAcceptedAsync(context.RequestServices.GetRequiredService<LocalAuthAudit>(), userId, verification.Method!,
                now, sourceIp, ct).ConfigureAwait(false);
            return (account, verification);
        }

        /// <summary>
        /// <c>POST {MfaPath}/disable</c> <c>{code | recoveryCode}</c>: removes the second factor and
        /// revokes every session of the user, the caller's included; the user signs in again (and under
        /// <see cref="LocalAuthOptions.RequireMfa"/> can reach only enrollment).
        /// </summary>
        private static async Task HandleDisableAsync(HttpContext context)
        {
            if (await PresentFactorAsync(context, onAccepted: null).ConfigureAwait(false) is not { } presented)
                return;
            var userId = presented.Account.State.UserId;
            var now = LocalAuthEndpoint.Now(context);
            var sourceIp = LocalAuthEndpoint.SourceIp(context);
            var ct = context.RequestAborted;
            await context.RequestServices.GetRequiredService<ILocalAuthStateStore>().DeleteMfaAsync(userId, ct).ConfigureAwait(false);
            await context.RequestServices.GetRequiredService<LocalAuthAudit>().RecordAsync(now, AuthAuditKind.MfaDisabled,
                AuthAuditOutcome.Success, userId, null, sourceIp, ReasonFor(presented.Verification.Method!), ct).ConfigureAwait(false);
            await context.RequestServices.GetRequiredService<LocalSessionManager>()
                .RevokeAllAsync(userId, LocalSessionRevokeReasons.MfaChanged, null, sourceIp, now, ct).ConfigureAwait(false);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        /// <summary>
        /// <c>POST {MfaPath}/recovery-codes</c> <c>{code | recoveryCode}</c>: replaces every recovery code
        /// (spent or not) with ten new ones, returned this once, in the same write that spends the
        /// presented factor. Revokes every other session.
        /// </summary>
        private static async Task HandleRegenerateAsync(HttpContext context)
        {
            var (codes, hashes) = LocalMfaService.NewRecoveryCodes();
            if (await PresentFactorAsync(context, s => s with { RecoveryCodeHashes = hashes }).ConfigureAwait(false) is not { } presented)
                return;
            var userId = presented.Account.State.UserId;
            var now = LocalAuthEndpoint.Now(context);
            var sourceIp = LocalAuthEndpoint.SourceIp(context);
            var ct = context.RequestAborted;
            await context.RequestServices.GetRequiredService<LocalAuthAudit>().RecordAsync(now, AuthAuditKind.MfaRecoveryCodesRegenerated,
                AuthAuditOutcome.Success, userId, null, sourceIp, null, ct).ConfigureAwait(false);
            await ReissueAsync(context, presented.Account,
                LocalAuthMethods.Password + " " + presented.Verification.Method, now, sourceIp).ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new LocalMfaRecoveryCodesResponse(codes), ct).ConfigureAwait(false);
        }
    }

    /// <summary>JSON body of <c>{MfaPath}/verify</c>: the login challenge ticket and one factor.</summary>
    public sealed record LocalMfaVerifyRequest
    {
        /// <summary>The ticket <c>/auth/login</c> returned with <c>mfa-required</c>.</summary>
        public string? Challenge { get; init; }

        /// <summary>The current code from the authenticator app.</summary>
        public string? Code { get; init; }

        /// <summary>A recovery code, instead of <see cref="Code"/>.</summary>
        public string? RecoveryCode { get; init; }
    }

    /// <summary>JSON body of <c>{MfaPath}/enroll/confirm</c>.</summary>
    public sealed record LocalMfaCodeRequest
    {
        /// <summary>The current code from the authenticator app just set up.</summary>
        public string? Code { get; init; }
    }

    /// <summary>JSON body of <c>{MfaPath}/disable</c> and <c>{MfaPath}/recovery-codes</c>: one current factor.</summary>
    public sealed record LocalMfaFactorRequest
    {
        /// <summary>The current code from the authenticator app.</summary>
        public string? Code { get; init; }

        /// <summary>A recovery code, instead of <see cref="Code"/>.</summary>
        public string? RecoveryCode { get; init; }
    }

    /// <summary>JSON body of <c>{MfaPath}/enroll</c>: an empty object.</summary>
    public sealed record LocalMfaEmptyRequest;

    /// <summary>
    /// 200 body of <c>{MfaPath}/enroll</c>. <see cref="Secret"/> (base32) is for typing into an
    /// authenticator by hand; <see cref="OtpAuthUri"/> is the same key as a URI to render as a QR code.
    /// </summary>
    public sealed record LocalMfaEnrollmentResponse(
        string Secret, string OtpAuthUri, DateTimeOffset ExpiresAt, string Algorithm, int Digits, int Period);

    /// <summary>200 body of <c>{MfaPath}/enroll/confirm</c> and <c>{MfaPath}/recovery-codes</c>: the recovery codes, shown once.</summary>
    public sealed record LocalMfaRecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);
}
