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
        public Task<IDictionary<string, object?>> AuthenticateAsync(
            HttpContext context, string requestedTable, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}
