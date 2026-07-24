using System.Security.Claims;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// A resolved directory account: the stored password <b>hash</b> a simple bind is verified
    /// against, the candidate <see cref="ClaimsPrincipal"/> the account maps to, and whether the
    /// account is enabled. The principal is the <i>candidate</i> identity only — it is still
    /// projected through <see cref="IBifrostAuthContextFactory"/>, which is where a subject-less or
    /// unmapped-issuer principal is rejected. A store must never hand back an anonymous or ambient
    /// identity to stand in for a failed lookup; it returns <c>null</c> instead.
    /// </summary>
    /// <param name="PasswordHash">
    /// The stored credential in a self-describing password-hash format (bcrypt <c>$2*</c> /
    /// Argon2id <c>$argon2id$</c>). It is a HASH, never a plaintext password — the bind path only
    /// ever hands it to <see cref="ILdapPasswordHasher.Verify"/>; there is no code path that compares
    /// a plaintext or that would store/read a cleartext credential column.
    /// </param>
    /// <param name="Principal">The candidate identity this account maps to on a successful verify.</param>
    /// <param name="Enabled">Whether the account may bind. A disabled account fails with the same
    /// uniform invalidCredentials result as an unknown DN or a wrong password.</param>
    public sealed record LdapCredentialRecord(string PasswordHash, ClaimsPrincipal Principal, bool Enabled);

    /// <summary>
    /// Resolves an LDAP bind DN to an <see cref="LdapCredentialRecord"/>. This is the pluggable
    /// identity source the LDAP front door authenticates simple binds against — a users table, a
    /// mapped directory, a local account store — with a single hard rule: an unknown DN resolves to
    /// <c>null</c> (the bind fails), never to an ambient/anonymous identity. There is deliberately no
    /// default registration: a deployment that enables bind authentication MUST supply one, so the
    /// front door can never come up with a default ambient credential store that authenticates
    /// everyone to nobody (criterion 1, fail-closed).
    /// </summary>
    public interface ILdapCredentialStore
    {
        /// <summary>
        /// Resolves the account for <paramref name="bindDn"/>, or returns <c>null</c> when no such
        /// account exists. Never returns a fallback identity. The caller verifies the presented
        /// password against the returned <see cref="LdapCredentialRecord.PasswordHash"/> — and runs
        /// an equivalent verify against a decoy hash when this returns <c>null</c> — so an unknown DN
        /// is timing-indistinguishable from a wrong password.
        /// </summary>
        Task<LdapCredentialRecord?> FindAsync(string bindDn, CancellationToken cancellationToken);
    }
}
