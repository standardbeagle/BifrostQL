using System.Security.Claims;
using BifrostQL.Core.Auth;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// Authenticates an OData HTTP request and projects the resolved principal through
    /// <see cref="IBifrostAuthContextFactory"/> — the same identity seam every other transport
    /// gate uses, fail-closed. Two credential shapes are accepted:
    /// <list type="bullet">
    /// <item><b>Bearer</b> (or any scheme validated upstream): the principal is already on
    /// <see cref="HttpContext.User"/>, populated by the host's authentication middleware. It is
    /// projected as-is; an unauthenticated request fails closed with 401.</item>
    /// <item><b>Basic</b>: the username is resolved through an optional
    /// <see cref="IODataBasicCredentialStore"/> and the password verified against the stored
    /// one-way <see cref="PasswordHasher{TUser}"/> hash before the resolved principal is
    /// projected. The store holds no plaintext-equivalent.</item>
    /// </list>
    ///
    /// <para>Security posture (see .claude/rules/protocol-adapter-security.md):</para>
    /// <list type="bullet">
    /// <item>The password verification runs UNCONDITIONALLY against a precomputed dummy hash
    /// when the username is unknown or disabled, so an unknown username is indistinguishable by
    /// timing or response from a known one with a wrong password (invariant 2). The
    /// existence/enabled check is ANDed AFTER the verification, never gated before it.</item>
    /// <item>Every client-fault path throws <see cref="ODataProtocolException"/> — the single
    /// type the middleware's catch filters on — so nothing escapes to the host on adversarial
    /// input (invariant 1).</item>
    /// <item>A subject-less principal, an unmapped OIDC issuer, or a projection yielding no
    /// identity all fail closed as 403 — never a degraded/anonymous context. No projection
    /// detail reaches the wire (logged server-side only; invariant 3).</item>
    /// </list>
    /// </summary>
    public sealed class ODataAuthenticator
    {
        // A precomputed PasswordHasher hash used only to spend the same PBKDF2 work on an
        // unknown/disabled username as a real verification does (mirroring LocalUserStore's
        // dummy hash). Without it, a missing credential returns before any
        // VerifyHashedPassword call, so the ~100 ms hashing cost becomes a timing oracle that
        // distinguishes "no such user" from "wrong password".
        private readonly string _dummyHash;
        private const string DummyUsername = "^@bifrost-odata-timing-guard";
        private const string DummyPassword = "^@bifrost-odata-timing-guard-password";
        private const string BasicPrefix = "Basic ";

        private readonly IBifrostAuthContextFactory _authFactory;
        private readonly IODataBasicCredentialStore? _basicStore;
        private readonly IPasswordHasher<string> _passwordHasher;
        private readonly ODataBasicAuthRateLimiter _basicAttempts;
        private readonly bool _allowCleartextBasicAuth;
        private readonly ILogger? _logger;

        /// <param name="options">Supplies the Basic attempt caps; null uses <see cref="ODataOptions"/> defaults.</param>
        public ODataAuthenticator(
            IBifrostAuthContextFactory authFactory,
            IODataBasicCredentialStore? basicStore = null,
            ILogger<ODataAuthenticator>? logger = null,
            IPasswordHasher<string>? passwordHasher = null,
            ODataOptions? options = null)
            : this(authFactory, basicStore, logger, passwordHasher, options ?? new ODataOptions(), clock: null)
        {
        }

        // The clock seam tests use to step the attempt window.
        internal ODataAuthenticator(
            IBifrostAuthContextFactory authFactory,
            IODataBasicCredentialStore? basicStore,
            ILogger<ODataAuthenticator>? logger,
            IPasswordHasher<string>? passwordHasher,
            ODataOptions options,
            Func<DateTimeOffset>? clock)
        {
            _authFactory = authFactory ?? throw new ArgumentNullException(nameof(authFactory));
            _basicStore = basicStore;
            _logger = logger;
            _passwordHasher = passwordHasher ?? new PasswordHasher<string>();
            _dummyHash = _passwordHasher.HashPassword(DummyUsername, DummyPassword);
            _basicAttempts = new ODataBasicAuthRateLimiter(
                options.MaxBasicAuthAttemptsPerSource, options.MaxBasicAuthAttemptsPerAccount,
                options.BasicAuthAttemptWindow, clock);
            _allowCleartextBasicAuth = options.AllowCleartextBasicAuth;

            // The middleware takes this authenticator as a constructor dependency, and the host
            // builds middleware when it builds the pipeline, so this warning fires at startup.
            if (_allowCleartextBasicAuth)
                _logger?.LogWarning(
                    "OData AllowCleartextBasicAuth is ON: Basic passwords are accepted on non-HTTPS " +
                    "requests and may cross the wire in the clear. Development only. Behind a " +
                    "TLS-terminating proxy, configure UseForwardedHeaders instead.");
        }

        /// <summary>
        /// Authenticates the request and returns the projected Bifrost user context on success.
        /// Throws <see cref="ODataProtocolException"/> on any auth failure: 401 for absent or
        /// invalid credentials, 403 for an authenticated-but-unacceptable identity.
        /// </summary>
        public async Task<IDictionary<string, object?>> AuthenticateAsync(HttpContext context, CancellationToken ct)
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith(BasicPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Refuse a Basic credential on cleartext before decoding it, charging the attempt
                // limiter or asking the store (new-front-door checklist), so the refusal never
                // varies by account. Request.IsHttps is the only transport signal read. Behind a
                // TLS-terminating proxy it is true only when the host's UseForwardedHeaders
                // middleware trusted that proxy's X-Forwarded-Proto; the raw header is
                // client-forgeable and never consulted here.
                if (!context.Request.IsHttps && !_allowCleartextBasicAuth)
                    throw ODataProtocolException.HttpsRequired();
                return await AuthenticateBasicAsync(context, authHeader, ct);
            }

            // Bearer (or any scheme the host's auth middleware already validated): the principal
            // is on HttpContext.User. An unauthenticated request fails closed with 401.
            if (context.User?.Identity?.IsAuthenticated != true)
                throw ODataProtocolException.Unauthorized();

            return await ProjectIdentityAsync(context, context.User, ct);
        }

        private async Task<IDictionary<string, object?>> AuthenticateBasicAsync(
            HttpContext context, string authHeader, CancellationToken ct)
        {
            var (username, password) = DecodeBasic(authHeader);

            // Basic auth is optional; without a store there is no identity source, so a Basic
            // request fails closed rather than degrading to anonymous.
            if (_basicStore is null)
                throw ODataProtocolException.Unauthorized("Basic authentication is not configured.");

            // Throttle per client IP and per username BEFORE the store lookup and the PBKDF2
            // verification: a refused guess costs no hashing, and the refusal reads the same
            // whether or not the account exists.
            var source = ProtocolSourceKey.Of(context.Connection.RemoteIpAddress is { } ip
                ? new System.Net.IPEndPoint(ip, context.Connection.RemotePort)
                : null);
            if (!_basicAttempts.TryAttempt(source, username, out var admission))
                throw ODataProtocolException.TooManyAuthenticationAttempts();

            var credential = await _basicStore.FindAsync(username, ct);
            var usable = credential is { Enabled: true };

            // UNCONDITIONAL PasswordHasher verification against the real hash when usable,
            // otherwise the precomputed dummy hash — the miss and wrong-password paths spend
            // the same PBKDF2 work, so account existence is not a timing oracle (invariant 2).
            // The existence/enabled check is ANDed only AFTER the verification has run.
            var hash = usable ? credential!.PasswordHash : _dummyHash;
            var passwordMatches =
                _passwordHasher.VerifyHashedPassword(username, hash, password) != PasswordVerificationResult.Failed;

            if (!(passwordMatches && usable))
                throw ODataProtocolException.Unauthorized("Invalid credentials.");

            // The credential verified, so this attempt was not a guess: give its slots back on
            // both axes. The window then counts only failures, so a client that sends Basic on
            // every page request is never refused for its own good logins. Only a verified
            // attempt is refunded; the refusal above already ran before any lookup or hashing.
            _basicAttempts.RefundVerified(admission);

            return await ProjectIdentityAsync(context, credential!.Principal, ct);
        }

        private static (string Username, string Password) DecodeBasic(string authHeader)
        {
            var encoded = authHeader.AsSpan(BasicPrefix.Length).Trim().ToString();
            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                throw ODataProtocolException.Unauthorized("Malformed Basic credentials.");
            }

            var decoded = Encoding.UTF8.GetString(raw);
            var separator = decoded.IndexOf(':');
            if (separator < 0)
                throw ODataProtocolException.Unauthorized("Malformed Basic credentials.");

            return (decoded[..separator], decoded[(separator + 1)..]);
        }

        /// <summary>
        /// Projects the resolved principal through the shared auth seam. A subject-less
        /// principal, an unmapped OIDC issuer, or a projection that yields no identity all fail
        /// closed as 403 — never a degraded/anonymous context.
        /// </summary>
        private async Task<IDictionary<string, object?>> ProjectIdentityAsync(
            HttpContext context, ClaimsPrincipal principal, CancellationToken ct)
        {
            try
            {
                context.User = principal;
                var projected = UserContexts.Copy(await _authFactory.CreateUserContextAsync(
                    context, context.RequestServices, ct));
                if (projected.Count == 0)
                {
                    _logger?.LogWarning("OData identity projected to an empty user context; rejecting.");
                    throw ODataProtocolException.Forbidden();
                }
                return projected;
            }
            catch (ODataProtocolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "OData identity projection failed; rejecting.");
                throw ODataProtocolException.Forbidden();
            }
        }
    }
}
