using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Feeds
{
    /// <summary>
    /// A deliberately user-facing feed authentication failure. EVERY failure class — a missing,
    /// malformed, unknown, disabled/revoked, expired, or table-mismatched feed token, and a
    /// candidate principal that projects to no identity — collapses to this ONE type carrying ONE
    /// fixed 401 status and ONE fixed generic message. The failure classes are therefore
    /// byte-identical on the wire, so the endpoint can never become an existence/validity oracle for
    /// feed tokens (.claude/rules/protocol-adapter-security.md invariants 9/10). The message is a
    /// fixed constant that never carries the token value, the table, or any request detail
    /// (invariant 3). The slice-4 endpoint that mounts this seam MUST include this type in its catch
    /// filter (invariant 1) and map it to a bare 401 with no distinguishing body.
    /// </summary>
    public sealed class FeedAuthException : Exception
    {
        /// <summary>The single HTTP status every feed auth failure maps to.</summary>
        public const int Status = 401;

        private const string UniformMessage = "Feed authentication failed.";

        private FeedAuthException() : base(UniformMessage) { }

        /// <summary>The one and only failure surface; identical for every failure class.</summary>
        public static FeedAuthException Unauthorized() => new();

        /// <summary>The HTTP status this failure maps to (always <see cref="Status"/>).</summary>
        public int HttpStatus => Status;
    }

    /// <summary>
    /// Authenticates a syndication-feed request and projects the resolved principal through
    /// <see cref="IBifrostAuthContextFactory"/> — the same identity seam every other transport gate
    /// uses, fail-closed. This is the testable seam the slice-4 HTTP endpoint mounts; it owns no
    /// route or middleware of its own.
    /// </summary>
    public sealed class FeedAuthenticator
    {
        private const string BearerPrefix = "Bearer ";
        private const string TokenQueryKey = "token";

        private readonly IBifrostAuthContextFactory _authFactory;
        private readonly IFeedCredentialStore? _tokenStore;
        private readonly ILogger? _logger;

        public FeedAuthenticator(
            IBifrostAuthContextFactory authFactory,
            IFeedCredentialStore? tokenStore = null,
            ILogger<FeedAuthenticator>? logger = null)
        {
            _authFactory = authFactory ?? throw new ArgumentNullException(nameof(authFactory));
            _tokenStore = tokenStore;
            _logger = logger;
        }

        /// <summary>
        /// Authenticates the request for <paramref name="requestedTable"/> and returns the projected
        /// Bifrost user context on success. Throws <see cref="FeedAuthException"/> (uniform 401) on
        /// any failure, minting no user context.
        /// </summary>
        public async Task<IDictionary<string, object?>> AuthenticateAsync(
            HttpContext context, string requestedTable, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(requestedTable);

            // Bearer path: the host's authentication middleware already validated the token and put
            // the principal on HttpContext.User. Project it as-is through the shared seam — a full
            // identity the pipeline scopes; no per-token table allow-list applies here.
            if (context.User?.Identity?.IsAuthenticated == true)
                return ProjectCandidate(context, context.User);

            // Scoped feed-token path. Read the token from the request (Bearer header the host did NOT
            // validate as a JWT, or the ?token= query param feed readers can carry), resolve it
            // through the host-supplied store, and validate the credential BEFORE projecting so a
            // failed check mints no user context. Every failure class collapses to one uniform 401.
            var token = ExtractToken(context);
            if (string.IsNullOrEmpty(token) || _tokenStore is null)
                throw FeedAuthException.Unauthorized();

            FeedCredential? credential;
            try
            {
                credential = await _tokenStore.ResolveAsync(token, cancellationToken);
            }
            catch (Exception ex)
            {
                // A store fault is internal — never forward its detail (which may embed the token or
                // DB text) onto the wire (invariant 3). Log server-side WITHOUT the token, fail closed.
                _logger?.LogWarning(ex, "Feed credential store threw resolving a feed token; failing closed.");
                throw FeedAuthException.Unauthorized();
            }

            if (!IsUsable(credential, requestedTable))
                throw FeedAuthException.Unauthorized();

            return ProjectCandidate(context, credential!.Principal);
        }

        /// <summary>
        /// Reads the raw feed token from the request: an unvalidated <c>Bearer</c> header first, then
        /// the <c>?token=</c> query param. Returns <c>null</c> when neither carries a value. The token
        /// is never logged or retained here.
        /// </summary>
        private static string? ExtractToken(HttpContext context)
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var headerToken = authHeader.AsSpan(BearerPrefix.Length).Trim().ToString();
                if (headerToken.Length > 0)
                    return headerToken;
            }

            var queryToken = context.Request.Query[TokenQueryKey].ToString();
            return string.IsNullOrEmpty(queryToken) ? null : queryToken;
        }

        /// <summary>
        /// Whether a resolved credential authorizes a read of <paramref name="requestedTable"/>: it
        /// must be non-null, enabled, unexpired, and scoped to the table (ordinal match). An empty
        /// allow-list matches nothing. Cheap boolean checks only — the variable-time token
        /// lookup/compare lives entirely behind the store (invariant 2), and every negative branch
        /// yields the same uniform 401 at the call site so no branch is a wire oracle (invariants 9/10).
        /// </summary>
        private static bool IsUsable(FeedCredential? credential, string requestedTable)
        {
            if (credential is not { Enabled: true })
                return false;
            if (credential.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
                return false;
            return credential.AllowedTables.Contains(requestedTable, StringComparer.Ordinal);
        }

        /// <summary>
        /// Projects the candidate principal SOLELY through the shared factory (the identity-only
        /// overload — never the merge overload — so raw token/query input cannot inject or shadow an
        /// identity claim). A subject-less principal, an unmapped OIDC issuer, or a projection that
        /// yields no identity all fail closed with the uniform 401; the detail is logged server-side
        /// only (invariants 3, 9/10).
        /// </summary>
        private IDictionary<string, object?> ProjectCandidate(HttpContext context, ClaimsPrincipal principal)
        {
            try
            {
                context.User = principal;
                var projected = _authFactory.CreateUserContext(context);
                if (projected.Count == 0)
                {
                    _logger?.LogWarning("Feed identity projected to an empty user context; failing closed.");
                    throw FeedAuthException.Unauthorized();
                }
                return projected;
            }
            catch (FeedAuthException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Feed identity projection failed; failing closed.");
                throw FeedAuthException.Unauthorized();
            }
        }
    }
}
