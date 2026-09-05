using System.Security.Claims;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// A resolved PostgreSQL login: the SCRAM verifier the wire authentication proves
    /// knowledge of, and the <see cref="ClaimsPrincipal"/> that login maps to. The
    /// principal is the <i>candidate</i> identity only — it is still projected through
    /// <see cref="IBifrostAuthContextFactory"/>, which is where a subject-less or
    /// unmapped-issuer principal is rejected. A store must never hand back an anonymous
    /// or ambient identity to stand in for a failed lookup; it returns <c>null</c> instead.
    /// </summary>
    /// <param name="Verifier">
    /// The SCRAM-SHA-256 verifier (salt, iterations, StoredKey, ServerKey) derived from the
    /// shared secret with <see cref="PgScramVerifier.Derive(string, int, int)"/>. The store
    /// never holds the plaintext secret: the SCRAM path verifies the client proof against
    /// the StoredKey directly, and the cleartext path re-derives and compares in constant
    /// time via <see cref="PgScramVerifier.VerifyPassword"/>.
    /// </param>
    /// <param name="Principal">The authenticated identity this login maps to on success.</param>
    public sealed record PgLogin(PgScramVerifier Verifier, ClaimsPrincipal Principal);

    /// <summary>
    /// Resolves a PostgreSQL startup username to a <see cref="PgLogin"/>. This is the
    /// pluggable identity source the pgwire front door authenticates against — an
    /// API-key table, an OIDC client-credentials directory, a local user store — with a
    /// single hard rule: an unknown user resolves to <c>null</c> (auth fails, connection
    /// rejected), never to an ambient/anonymous identity. There is deliberately no default
    /// registration: a deployment that mounts the pgwire adapter must supply one, so the
    /// front door can never come up authenticating everyone to nobody.
    /// </summary>
    public interface IPgCredentialStore
    {
        /// <summary>
        /// Resolves the login for <paramref name="username"/>, or returns <c>null</c> when
        /// no such user exists. Never returns a fallback identity.
        /// </summary>
        Task<PgLogin?> FindAsync(string username, CancellationToken cancellationToken);
    }
}
