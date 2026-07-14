using System.Security.Claims;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// A resolved Redis login: the shared secret the <c>AUTH</c> command proves knowledge
    /// of, and the <see cref="ClaimsPrincipal"/> that login maps to. The principal is the
    /// <i>candidate</i> identity only — it is still projected through
    /// <see cref="IBifrostAuthContextFactory"/>, which is where a subject-less or
    /// unmapped-issuer principal is rejected. A store must never hand back an anonymous or
    /// ambient identity to stand in for a failed lookup; it returns <c>null</c> instead.
    /// </summary>
    /// <param name="Secret">
    /// The shared secret (password / API key / client secret) the <c>AUTH</c> password is
    /// compared against in constant time.
    /// </param>
    /// <param name="Principal">The authenticated identity this login maps to on success.</param>
    public sealed record RespLogin(string Secret, ClaimsPrincipal Principal);

    /// <summary>
    /// Resolves a Redis <c>AUTH</c> username to a <see cref="RespLogin"/>. This is the
    /// pluggable identity source the RESP front door authenticates against — an API-key
    /// table, an OIDC client-credentials directory, a local user store — with a single hard
    /// rule: an unknown user resolves to <c>null</c> (auth fails, connection stays
    /// unauthenticated), never to an ambient/anonymous identity. There is deliberately no
    /// default registration: a deployment that mounts the RESP adapter with authentication
    /// required must supply one, so the front door can never come up authenticating everyone
    /// to nobody. An <c>AUTH &lt;password&gt;</c> with no username resolves the Redis-default
    /// <c>default</c> user through this same store.
    /// </summary>
    public interface IRespCredentialStore
    {
        /// <summary>
        /// Resolves the login for <paramref name="username"/>, or returns <c>null</c> when no
        /// such user exists. Never returns a fallback identity.
        /// </summary>
        /// <remarks>
        /// Implementations MUST perform a constant-time lookup and MUST NOT short-circuit on an
        /// unknown user: returning <c>null</c> faster for a missing username than a present one
        /// reintroduces a user-existence timing oracle that the connection handler's
        /// <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals"/>
        /// decoy compare is specifically there to close. Mirror the pgwire credential-store
        /// contract: the cost of a lookup must not reveal whether the account exists.
        /// </remarks>
        Task<RespLogin?> FindAsync(string username, CancellationToken cancellationToken);
    }
}
