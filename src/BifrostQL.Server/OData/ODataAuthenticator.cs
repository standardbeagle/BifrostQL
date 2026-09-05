using System.Security.Claims;
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
        private readonly ILogger? _logger;

        public ODataAuthenticator(
            IBifrostAuthContextFactory authFactory,
            IODataBasicCredentialStore? basicStore = null,
            ILogger<ODataAuthenticator>? logger = null,
            IPasswordHasher<string>? passwordHasher = null)
        {
            _authFactory = authFactory ?? throw new ArgumentNullException(nameof(authFactory));
            _basicStore = basicStore;
            _logger = logger;
            _passwordHasher = passwordHasher ?? new PasswordHasher<string>();
            _dummyHash = _passwordHasher.HashPassword(DummyUsername, DummyPassword);
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
                return await AuthenticateBasicAsync(context, authHeader, ct);

            // Bearer (or any scheme the host's auth middleware already validated): the principal
            // is on HttpContext.User. An unauthenticated request fails closed with 401.
            if (context.User?.Identity?.IsAuthenticated != true)
                throw ODataProtocolException.Unauthorized();

            return ProjectIdentity(context, context.User);
        }

        private async Task<IDictionary<string, object?>> AuthenticateBasicAsync(
            HttpContext context, string authHeader, CancellationToken ct)
        {
            var (username, password) = DecodeBasic(authHeader);

            // Basic auth is optional; without a store there is no identity source, so a Basic
            // request fails closed rather than degrading to anonymous.
            if (_basicStore is null)
                throw ODataProtocolException.Unauthorized("Basic authentication is not configured.");

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

            return ProjectIdentity(context, credential!.Principal);
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
        private IDictionary<string, object?> ProjectIdentity(HttpContext context, ClaimsPrincipal principal)
        {
            try
            {
                context.User = principal;
                var projected = _authFactory.CreateUserContext(context);
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
