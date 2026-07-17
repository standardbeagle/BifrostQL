using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
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
    /// <see cref="IODataBasicCredentialStore"/> and the password compared in constant time
    /// before the resolved principal is projected.</item>
    /// </list>
    ///
    /// <para>Security posture (see .claude/rules/protocol-adapter-security.md):</para>
    /// <list type="bullet">
    /// <item>The password comparison runs UNCONDITIONALLY against a decoy secret when the
    /// username is unknown or disabled, so an unknown username is indistinguishable by timing or
    /// response from a known one with a wrong password (invariant 2). The existence/enabled
    /// check is ANDed AFTER the constant-time compare, never gated before it. Both sides are
    /// SHA-256 digested first, so the compare is fixed-length and password length is not
    /// leaked.</item>
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
        // A fixed, non-secret decoy used to keep the compare work identical for an
        // unknown/disabled username. Its only requirement is that a real client cannot know it,
        // which holds because it never leaves the process and no credential is provisioned with it.
        private const string DecoySecret = "bifrost-odata-decoy-secret-not-a-real-credential";
        private const string BasicPrefix = "Basic ";

        private readonly IBifrostAuthContextFactory _authFactory;
        private readonly IODataBasicCredentialStore? _basicStore;
        private readonly ILogger? _logger;

        public ODataAuthenticator(
            IBifrostAuthContextFactory authFactory,
            IODataBasicCredentialStore? basicStore = null,
            ILogger<ODataAuthenticator>? logger = null)
        {
            _authFactory = authFactory ?? throw new ArgumentNullException(nameof(authFactory));
            _basicStore = basicStore;
            _logger = logger;
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

            // UNCONDITIONAL constant-time compare against the real secret when usable, otherwise
            // a decoy. Both sides are SHA-256 digested so the compare is fixed-length regardless
            // of password length. The existence/enabled check is ANDed only AFTER the compare has
            // run, so an unknown username does the same work as a known one (invariant 2).
            var secret = usable ? credential!.Secret : DecoySecret;
            var passwordMatches = CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
                SHA256.HashData(Encoding.UTF8.GetBytes(password)));

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
