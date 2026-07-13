using System.Security.Cryptography;
using System.Text;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// A minimal SCRAM-SHA-256 client used by the tests to complete a real exchange
    /// against <c>ScramSha256Server</c> / the pgwire handler — the same computation a
    /// PostgreSQL driver performs. Deliberately independent of the server code so a bug
    /// in one cannot mask a bug in the other.
    /// </summary>
    internal sealed class ScramTestClient
    {
        private readonly string _clientNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        private string _clientFirstBare = "";

        public string ClientFirstMessage()
        {
            _clientFirstBare = $"n=,r={_clientNonce}";
            return "n,," + _clientFirstBare;
        }

        /// <summary>
        /// Builds the client-final-message for <paramref name="serverFirst"/> using
        /// <paramref name="password"/>, and returns the ServerSignature the server is
        /// expected to send back (<c>v=…</c> without the prefix) for verification.
        /// </summary>
        public string ClientFinalMessage(string serverFirst, string password, out string expectedServerSignature)
        {
            var fields = serverFirst.Split(',');
            var combinedNonce = Field(fields, 'r');
            var salt = Convert.FromBase64String(Field(fields, 's'));
            var iterations = int.Parse(Field(fields, 'i'));

            var saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
            var clientKey = Hmac(saltedPassword, "Client Key");
            var storedKey = SHA256.HashData(clientKey);

            var clientFinalWithoutProof = $"c=biws,r={combinedNonce}";
            var authMessage = $"{_clientFirstBare},{serverFirst},{clientFinalWithoutProof}";
            var authBytes = Encoding.UTF8.GetBytes(authMessage);
            var clientSignature = Hmac(storedKey, authMessage);

            var proof = new byte[clientKey.Length];
            for (var i = 0; i < proof.Length; i++)
                proof[i] = (byte)(clientKey[i] ^ clientSignature[i]);

            var serverKey = Hmac(saltedPassword, "Server Key");
            expectedServerSignature = Convert.ToBase64String(new HMACSHA256(serverKey).ComputeHash(authBytes));

            return $"{clientFinalWithoutProof},p={Convert.ToBase64String(proof)}";
        }

        private static byte[] Hmac(byte[] key, string message)
            => new HMACSHA256(key).ComputeHash(Encoding.UTF8.GetBytes(message));

        private static string Field(string[] fields, char key)
        {
            foreach (var f in fields)
                if (f.Length >= 2 && f[0] == key && f[1] == '=')
                    return f[2..];
            throw new InvalidOperationException($"SCRAM field '{key}' not found in server message.");
        }
    }
}
