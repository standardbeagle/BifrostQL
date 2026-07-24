namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Verifies a presented password against a stored password hash. This is the crypto primitive the
    /// bind path depends on, kept pluggable so a deployment supplies its chosen adaptive/memory-hard
    /// algorithm (bcrypt / Argon2id) from its crypto library. There is deliberately NO default
    /// registration and NO plaintext-comparison implementation shipped: the only way to authenticate a
    /// bind is to register a hasher that verifies a hash, so a plaintext comparison or a cleartext
    /// credential column is impossible by construction (criterion 1). A deployment that enables bind
    /// authentication must register both this and an <see cref="ILdapCredentialStore"/>, or binds fail
    /// closed.
    /// </summary>
    public interface ILdapPasswordHasher
    {
        /// <summary>
        /// Returns whether <paramref name="password"/> verifies against <paramref name="passwordHash"/>.
        /// Implementations MUST derive/compare using the hash's own algorithm and parameters (never a
        /// plaintext equality), and SHOULD run in time that depends on the hash cost, not on whether
        /// the password matches — so a decoy-hash verify for an unknown DN costs the same as a real one.
        /// Implementations must not throw on a malformed stored hash: treat it as a non-match (a
        /// corrupt hash must fail the bind, never leak a fault to the wire — invariant 3).
        /// </summary>
        bool Verify(ReadOnlySpan<byte> password, string passwordHash);

        /// <summary>
        /// A valid hash of a random secret this implementation never accepts, used as the verify target
        /// for an unknown DN so the reject path performs the same adaptive-hash work as a real account
        /// (anti-enumeration, invariant 2). It must be in the same format and cost class the hasher
        /// verifies, so the decoy verify is indistinguishable in work from a genuine one.
        /// </summary>
        string DecoyHash { get; }
    }
}
