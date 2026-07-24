using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// The LDAP simple-bind credential verifier (criterion 1). It resolves the configured account via
    /// a pluggable <see cref="ILdapCredentialStore"/>, verifies the presented password against the
    /// stored HASH via a pluggable <see cref="ILdapPasswordHasher"/> (never a plaintext compare, no
    /// default ambient store — both seams are required, so binds fail closed until a deployment wires
    /// them), and projects the candidate principal through the shared
    /// <see cref="IBifrostAuthContextFactory"/> every transport uses.
    ///
    /// <para><b>Anti-enumeration (invariant 2).</b> The adaptive-hash verify runs UNCONDITIONALLY
    /// against the account's hash or a decoy hash, and the existence/enabled checks are AND-ed AFTER
    /// the compare — never short-circuited before it. So an unknown DN, a wrong password, and a
    /// disabled account all perform the same hash work and are timing-indistinguishable, and every
    /// failure class (those three plus a subject-less principal and an unmapped issuer) returns the
    /// SAME uniform invalidCredentials result (criterion 2).</para>
    ///
    /// <para><b>Hardening (criterion 3).</b> A per-source and per-account rate limit and an oversized
    /// password are both refused BEFORE any hash work (a rate-limit trip or a hash-DoS attempt costs
    /// nothing); the presented password is zeroed after the verify; every attempt is reported to the
    /// optional <see cref="ILdapBindObserver"/> as a secret-free audit record that also drives the
    /// deployment's lockout policy. No verifier/store fault ever reaches the wire — a store exception
    /// is logged server-side and the bind fails as invalidCredentials (invariant 3).</para>
    /// </summary>
    internal sealed class LdapBindAuthenticator
    {
        private readonly ILdapCredentialStore _store;
        private readonly ILdapPasswordHasher _hasher;
        private readonly IBifrostAuthContextFactory _authFactory;
        private readonly LdapWireOptions _options;
        private readonly IServiceProvider? _services;
        private readonly LdapBindRateLimiter _rateLimiter;
        private readonly ILdapBindObserver? _observer;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ILogger _logger;

        public LdapBindAuthenticator(
            ILdapCredentialStore store,
            ILdapPasswordHasher hasher,
            IBifrostAuthContextFactory authFactory,
            LdapWireOptions options,
            IServiceProvider? services = null,
            LdapBindRateLimiter? rateLimiter = null,
            ILdapBindObserver? observer = null,
            Func<DateTimeOffset>? clock = null,
            ILogger? logger = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
            _authFactory = authFactory ?? throw new ArgumentNullException(nameof(authFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _services = services;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _rateLimiter = rateLimiter ?? new LdapBindRateLimiter(
                options.MaxBindAttemptsPerSource, options.MaxBindAttemptsPerAccount, options.BindRateLimitWindow, _clock);
            _observer = observer;
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// Authenticates one bind from <paramref name="source"/> (the connection's remote endpoint, the
        /// per-source rate-limit key). Returns a uniform success/anonymous/invalidCredentials result;
        /// the presented password is zeroed before this returns on every path.
        /// </summary>
        public async Task<LdapBindResult> AuthenticateAsync(LdapBindRequest bind, string source, CancellationToken ct)
        {
            var account = bind.Name ?? string.Empty;
            var password = bind.SimplePassword;
            try
            {
                // Anonymous bind (zero-length DN + zero-length password): gated separately (criterion 4).
                if (IsAnonymous(bind))
                    return Anonymous(account, source);

                // Rate limit BOTH axes before any hash work; a trip costs nothing (criterion 3).
                if (!_rateLimiter.TryBind(source, account))
                    return Fail(LdapBindOutcome.RateLimited, account, source);

                // Only simple binds with a non-empty, in-bound password are credentialed here. A SASL/
                // unknown choice (password null), an empty password (unauthenticated bind), or an
                // oversized password (hash-DoS) is refused BEFORE hashing — but with the SAME uniform
                // invalidCredentials result, so none is a distinguishable signal.
                if (password is null || password.Length == 0)
                    return Fail(LdapBindOutcome.InvalidCredentials, account, source);
                if (password.Length > _options.MaxPasswordLength)
                    return Fail(LdapBindOutcome.PasswordTooLong, account, source);

                // Resolve. A store fault must never reach the wire (invariant 3): log it, treat the DN
                // as unknown, and still run the decoy verify below so the timing profile is unchanged.
                LdapCredentialRecord? record;
                try
                {
                    record = await _store.FindAsync(account, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ldap credential store fault resolving a bind DN; failing closed.");
                    record = null;
                }

                // UNCONDITIONAL adaptive-hash verify against the real hash or a decoy (invariant 2).
                // Existence + enabled are AND-ed AFTER the compare, never short-circuited before it —
                // reordering these into `record is not null && Verify(...)` would skip the hash work for
                // an unknown DN and reintroduce the timing/branch enumeration oracle this prevents.
                var matches = _hasher.Verify(password, record?.PasswordHash ?? _hasher.DecoyHash);
                var credentialOk = record is not null && record.Enabled && matches;
                if (!credentialOk)
                    return Fail(LdapBindOutcome.InvalidCredentials, account, source);

                // Project the candidate principal through the shared factory. A subject-less principal
                // projects to an empty context (rejected); an unmapped issuer throws (rejected). Both
                // are failure classes 4 & 5 and return the same uniform invalidCredentials — never a
                // degraded anonymous identity.
                if (!TryProjectIdentity(record!.Principal, out var userContext))
                    return Fail(LdapBindOutcome.InvalidCredentials, account, source);

                Audit(LdapBindOutcome.Success, account, source);
                return LdapBindResult.Success(userContext);
            }
            finally
            {
                // Secret hygiene: wipe the presented password on every path (success or failure).
                if (password is { Length: > 0 })
                    CryptographicOperations.ZeroMemory(password);
            }
        }

        // Anonymous = RFC 4513 anonymous bind: a simple auth choice with an empty name AND an empty
        // password. A non-empty name with an empty password is an "unauthenticated bind", refused as
        // invalidCredentials by the empty-password check on the credentialed path.
        private static bool IsAnonymous(LdapBindRequest bind) =>
            bind.AuthKind == LdapBindAuthKind.Simple
            && string.IsNullOrEmpty(bind.Name)
            && bind.SimplePassword is { Length: 0 };

        private LdapBindResult Anonymous(string account, string source)
        {
            if (!_options.AnonymousBindEnabled)
                return Fail(LdapBindOutcome.AnonymousDisabled, account, source);
            Audit(LdapBindOutcome.AnonymousSuccess, account, source);
            return LdapBindResult.Anonymous();
        }

        private LdapBindResult Fail(LdapBindOutcome outcome, string account, string source)
        {
            Audit(outcome, account, source);
            return LdapBindResult.Invalid();
        }

        private void Audit(LdapBindOutcome outcome, string account, string source)
        {
            try
            {
                _observer?.OnBind(new LdapBindAuditRecord(outcome, account, source, _clock()));
            }
            catch (Exception ex)
            {
                // The audit/lockout hook is best-effort; a faulty observer must never break the bind.
                _logger.LogWarning(ex, "ldap bind observer threw; ignoring.");
            }
        }

        /// <summary>
        /// Projects the candidate principal through the shared auth seam. Returns false (fail closed)
        /// when projection throws (unmapped issuer) or yields no identity (subject-less) — never a
        /// degraded anonymous context.
        /// </summary>
        private bool TryProjectIdentity(System.Security.Claims.ClaimsPrincipal principal, out IDictionary<string, object?> userContext)
        {
            userContext = new Dictionary<string, object?>();
            try
            {
                var carrier = new DefaultHttpContext { RequestServices = _services!, User = principal };
                var projected = _authFactory.CreateUserContext(carrier);
                if (projected.Count == 0)
                {
                    _logger.LogWarning("ldap bind projected to an empty user context; rejecting.");
                    return false;
                }
                userContext = projected;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ldap identity projection failed; rejecting bind.");
                return false;
            }
        }
    }
}
