using System.Security.Cryptography;
using System.Text;

namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// Session ids and the fingerprints the session store keeps instead of secrets. A session id is
    /// 256 random bits; the store only ever sees its SHA-256, so a copy of the sessions table cannot
    /// be replayed as cookies. The credential stamp is the SHA-256 of the stored password hash: it
    /// changes when the password does, and is never the hash itself.
    /// </summary>
    internal static class LocalSessionId
    {
        public static string New() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        public static string Hash(string sessionId) => Sha256Hex(sessionId);

        public static string CredentialStamp(string passwordHash) => Sha256Hex(passwordHash);

        private static string Sha256Hex(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

        private static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
