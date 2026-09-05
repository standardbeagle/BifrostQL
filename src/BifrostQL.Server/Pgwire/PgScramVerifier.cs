using System.Security.Cryptography;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// A SCRAM-SHA-256 verifier (RFC 5802 §3): the salt, iteration count, StoredKey and
    /// ServerKey derived from a password. This is ALL the credential store holds — the
    /// plaintext secret never needs to be stored, because the server-side exchange verifies
    /// the client's proof against <see cref="StoredKey"/> and proves itself to the client
    /// with <see cref="ServerKey"/>, neither of which is sufficient to impersonate the user.
    ///
    /// <para>Provision one with <see cref="Derive(string, int, int)"/>; verify a cleartext-path
    /// password against one with <see cref="VerifyPassword"/>.</para>
    /// </summary>
    /// <param name="Salt">The PBKDF2 salt the password was derived with.</param>
    /// <param name="Iterations">The PBKDF2 iteration count the password was derived with.</param>
    /// <param name="StoredKey">SHA-256 of the derived Client Key — the proof-verification key.</param>
    /// <param name="ServerKey">HMAC key for the server signature the client checks.</param>
    public sealed record PgScramVerifier(byte[] Salt, int Iterations, byte[] StoredKey, byte[] ServerKey)
    {
        /// <summary>The iteration count used by <see cref="Derive(string, int, int)"/> and by
        /// the decoy verifier for unknown users, so both sides of the timing invariant match.</summary>
        public const int DefaultIterations = 4096;

        private const int KeyLength = 32; // SHA-256 output / SaltedPassword length
        private static readonly byte[] ClientKeyLabel = System.Text.Encoding.ASCII.GetBytes("Client Key");
        private static readonly byte[] ServerKeyLabel = System.Text.Encoding.ASCII.GetBytes("Server Key");

        /// <summary>Derives a verifier from a password with a fresh random salt.</summary>
        public static PgScramVerifier Derive(string password, int iterations = DefaultIterations, int saltBytes = SaltLength)
            => Derive(password, RandomNumberGenerator.GetBytes(saltBytes), iterations);

        /// <summary>Derives a verifier from a password with an explicit salt (deterministic tests).</summary>
        public static PgScramVerifier Derive(string password, byte[] salt, int iterations)
        {
            if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
            ArgumentNullException.ThrowIfNull(password);
            ArgumentNullException.ThrowIfNull(salt);
            var saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
                System.Text.Encoding.UTF8.GetBytes(password), salt, iterations,
                HashAlgorithmName.SHA256, KeyLength);
            var clientKey = HMACSHA256.HashData(saltedPassword, ClientKeyLabel);
            return new PgScramVerifier(
                salt, iterations,
                StoredKey: SHA256.HashData(clientKey),
                ServerKey: HMACSHA256.HashData(saltedPassword, ServerKeyLabel));
        }

        /// <summary>
        /// Verifies a cleartext-path password against this verifier: re-derives the
        /// SaltedPassword with the stored salt and iteration count and compares the resulting
        /// StoredKey in constant time. Costs one PBKDF2 run either way, so a miss and a
        /// wrong password are timing-indistinguishable.
        /// </summary>
        public bool VerifyPassword(string password)
        {
            var derived = Derive(password, Salt, Iterations);
            return CryptographicOperations.FixedTimeEquals(derived.StoredKey, StoredKey);
        }

        private const int SaltLength = 16;
        // Per-process key for the decoy salt. A real user's verifier has ONE salt, so its
        // server-first message advertises the same s= on every connection; the decoy salt
        // must therefore be a deterministic function of the username (RFC 5802 §5.1,
        // RFC 7677 — Postgres derives its mock salt from a per-cluster nonce the same way),
        // or two connections as the same unknown name reveal the user by salt change.
        private static readonly byte[] DecoySaltKey = RandomNumberGenerator.GetBytes(KeyLength);

        /// <summary>
        /// A structurally identical verifier for an unknown user: a salt derived
        /// deterministically from the username, the default iteration count, and random keys
        /// no proof can match. The exchange then fails at the proof step with the same work
        /// and the same wire shape as a wrong password (no user-existence oracle, invariant 2).
        /// </summary>
        internal static PgScramVerifier Decoy(string username)
            => new(HMACSHA256.HashData(DecoySaltKey, System.Text.Encoding.UTF8.GetBytes(username))[..SaltLength],
                DefaultIterations,
                RandomNumberGenerator.GetBytes(KeyLength), RandomNumberGenerator.GetBytes(KeyLength));
    }
}
