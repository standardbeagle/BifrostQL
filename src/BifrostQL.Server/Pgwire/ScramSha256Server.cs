using System.Security.Cryptography;
using System.Text;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Server side of one SCRAM-SHA-256 (RFC 5802 / RFC 7677) SASL exchange, as the
    /// PostgreSQL <c>AuthenticationSASL</c> flow uses it. The server proves nothing is
    /// sent in the clear: it holds the shared secret (the credential store's password),
    /// derives the salted password with PBKDF2, and verifies the client's proof against
    /// a freshly generated per-exchange nonce and salt.
    ///
    /// <para>State machine: construct, then <see cref="HandleClientFirst"/> →
    /// server-first, then <see cref="HandleClientFinal"/> → server-final (or throw on a
    /// bad proof). One instance handles exactly one connection's exchange; it is not
    /// reusable and not thread-safe.</para>
    ///
    /// <para>SASLprep (RFC 4013) normalization of the password is intentionally omitted:
    /// the credential store owns the canonical secret form, and the handshake compares
    /// against that exact secret on both the cleartext and SCRAM paths, so no drift
    /// arises between them. ASCII secrets — the common API-key/client-secret shape —
    /// are unaffected either way.</para>
    /// </summary>
    internal sealed class ScramSha256Server
    {
        private const int KeyLength = 32; // SHA-256 output / SaltedPassword length
        private static readonly byte[] ClientKeyLabel = Encoding.ASCII.GetBytes("Client Key");
        private static readonly byte[] ServerKeyLabel = Encoding.ASCII.GetBytes("Server Key");

        private readonly byte[] _password;
        private readonly byte[] _salt;
        private readonly int _iterations;

        private string? _clientFirstBare;
        private string? _serverFirstMessage;
        private string? _combinedNonce;

        public ScramSha256Server(string password, byte[] salt, int iterations)
        {
            if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
            _password = Encoding.UTF8.GetBytes(password ?? throw new ArgumentNullException(nameof(password)));
            _salt = salt ?? throw new ArgumentNullException(nameof(salt));
            _iterations = iterations;
        }

        /// <summary>Generates a random per-exchange server nonce and salt with sane defaults.</summary>
        public static ScramSha256Server Create(string password, int iterations = 4096, int saltBytes = 16)
            => new(password, RandomNumberGenerator.GetBytes(saltBytes), iterations);

        /// <summary>
        /// Consumes the client-first-message (full, gs2 header included) and returns the
        /// server-first-message to send in <c>AuthenticationSASLContinue</c>. Throws
        /// <see cref="PgScramProtocolException"/> on a malformed message.
        /// </summary>
        public string HandleClientFirst(string clientFirstMessage)
        {
            // client-first = gs2-header + client-first-bare. gs2-header is
            // "<cbind>,<authzid>," e.g. "n,," / "y,," / "p=tls-server-end-point,,".
            var gs2End = NthCommaIndex(clientFirstMessage, 2);
            if (gs2End < 0)
                throw new PgScramProtocolException("SCRAM client-first-message missing GS2 header.");

            _clientFirstBare = clientFirstMessage[(gs2End + 1)..];
            var clientNonce = FieldValue(_clientFirstBare, 'r')
                ?? throw new PgScramProtocolException("SCRAM client-first-message missing nonce (r=).");

            // The combined nonce = client nonce followed by fresh server nonce; the
            // client must echo it verbatim in client-final, binding the two rounds.
            var serverNonce = Base64Nonce();
            _combinedNonce = clientNonce + serverNonce;
            _serverFirstMessage =
                $"r={_combinedNonce},s={Convert.ToBase64String(_salt)},i={_iterations}";
            return _serverFirstMessage;
        }

        /// <summary>
        /// Verifies the client-final-message proof and returns the server-final-message
        /// (<c>v=&lt;ServerSignature&gt;</c>) to send in <c>AuthenticationSASLFinal</c>.
        /// Throws <see cref="PgScramAuthenticationException"/> when the proof does not
        /// match the shared secret (wrong password), and
        /// <see cref="PgScramProtocolException"/> on a malformed message or nonce mismatch.
        /// </summary>
        public string HandleClientFinal(string clientFinalMessage)
        {
            if (_clientFirstBare is null || _serverFirstMessage is null || _combinedNonce is null)
                throw new InvalidOperationException("HandleClientFirst must run before HandleClientFinal.");

            var proofField = FieldValue(clientFinalMessage, 'p')
                ?? throw new PgScramProtocolException("SCRAM client-final-message missing proof (p=).");
            var replayedNonce = FieldValue(clientFinalMessage, 'r')
                ?? throw new PgScramProtocolException("SCRAM client-final-message missing nonce (r=).");
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(replayedNonce), Encoding.ASCII.GetBytes(_combinedNonce)))
                throw new PgScramProtocolException("SCRAM nonce mismatch between rounds.");

            // client-final-message-without-proof is everything up to ",p=".
            var proofMarker = clientFinalMessage.LastIndexOf(",p=", StringComparison.Ordinal);
            if (proofMarker < 0)
                throw new PgScramProtocolException("SCRAM client-final-message malformed.");
            var clientFinalWithoutProof = clientFinalMessage[..proofMarker];

            var saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
                _password, _salt, _iterations, HashAlgorithmName.SHA256, KeyLength);
            var clientKey = Hmac(saltedPassword, ClientKeyLabel);
            var storedKey = SHA256.HashData(clientKey);

            var authMessage = Encoding.UTF8.GetBytes(
                $"{_clientFirstBare},{_serverFirstMessage},{clientFinalWithoutProof}");
            var clientSignature = Hmac(storedKey, authMessage);

            // ClientProof = ClientKey XOR ClientSignature. Recover the client's key and
            // check its SHA-256 equals our stored key — constant-time, fail closed.
            byte[] clientProof;
            try
            {
                clientProof = Convert.FromBase64String(proofField);
            }
            catch (FormatException)
            {
                throw new PgScramProtocolException("SCRAM client proof is not valid base64.");
            }
            if (clientProof.Length != clientKey.Length)
                throw new PgScramAuthenticationException("SCRAM proof length mismatch.");

            var recoveredClientKey = Xor(clientProof, clientSignature);
            var recoveredStoredKey = SHA256.HashData(recoveredClientKey);
            if (!CryptographicOperations.FixedTimeEquals(recoveredStoredKey, storedKey))
                throw new PgScramAuthenticationException("SCRAM proof verification failed.");

            var serverKey = Hmac(saltedPassword, ServerKeyLabel);
            var serverSignature = Hmac(serverKey, authMessage);
            return $"v={Convert.ToBase64String(serverSignature)}";
        }

        private static byte[] Hmac(byte[] key, byte[] message)
            => new HMACSHA256(key).ComputeHash(message);

        private static byte[] Xor(byte[] a, byte[] b)
        {
            var result = new byte[a.Length];
            for (var i = 0; i < a.Length; i++)
                result[i] = (byte)(a[i] ^ b[i]);
            return result;
        }

        private static string Base64Nonce()
            => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

        /// <summary>Reads a <c>key=value</c> field's value from a comma-separated SCRAM message.</summary>
        private static string? FieldValue(string message, char key)
        {
            foreach (var part in message.Split(','))
                if (part.Length >= 2 && part[0] == key && part[1] == '=')
                    return part[2..];
            return null;
        }

        private static int NthCommaIndex(string s, int n)
        {
            var index = -1;
            for (var i = 0; i < n; i++)
            {
                index = s.IndexOf(',', index + 1);
                if (index < 0) return -1;
            }
            return index;
        }
    }

    /// <summary>A SCRAM message was malformed or violated the protocol (client fault, protocol_violation).</summary>
    internal sealed class PgScramProtocolException : Exception
    {
        public PgScramProtocolException(string message) : base(message) { }
    }

    /// <summary>The client's SCRAM proof did not verify against the shared secret (wrong password).</summary>
    internal sealed class PgScramAuthenticationException : Exception
    {
        public PgScramAuthenticationException(string message) : base(message) { }
    }
}
