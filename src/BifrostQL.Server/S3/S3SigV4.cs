using System.Security.Cryptography;
using System.Text;

namespace BifrostQL.Server.S3
{
    /// <summary>
    /// AWS Signature Version 4 canonical-request and signing-key math (SigV4, non-goal:
    /// no SigV2, no streaming-chunk signatures). Pure, side-effect-free helpers shared by
    /// the verifier (which recomputes the expected signature from a resolved secret or a
    /// decoy) and by tests that need to construct validly signed requests.
    /// </summary>
    public static class S3SigV4
    {
        public const string Algorithm = "AWS4-HMAC-SHA256";
        public const string Service = "s3";
        public const string Terminator = "aws4_request";
        public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

        /// <summary>
        /// Canonical request per the SigV4 spec: method, canonical URI, canonical query
        /// string, canonical (sorted, lower-cased, trimmed) headers, signed-headers list,
        /// and the hashed payload — newline-joined.
        /// </summary>
        public static string BuildCanonicalRequest(
            string method,
            string canonicalUri,
            string canonicalQueryString,
            IReadOnlyList<(string Name, string Value)> sortedLowerHeaders,
            string signedHeaders,
            string hashedPayload)
        {
            var canonicalHeaders = new StringBuilder();
            foreach (var (name, value) in sortedLowerHeaders)
                canonicalHeaders.Append(name).Append(':').Append(value).Append('\n');

            return string.Join('\n',
                method,
                canonicalUri,
                canonicalQueryString,
                canonicalHeaders.ToString(),
                signedHeaders,
                hashedPayload);
        }

        public static string HashSha256Hex(string payload) => HashSha256Hex(Encoding.UTF8.GetBytes(payload));

        public static string HashSha256Hex(byte[] payload)
            => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        public static string StringToSign(string amzDate, string credentialScope, string canonicalRequest)
            => string.Join('\n', Algorithm, amzDate, credentialScope, HashSha256Hex(canonicalRequest));

        public static string CredentialScope(string dateStamp, string region) => $"{dateStamp}/{region}/{Service}/{Terminator}";

        /// <summary>Derives the SigV4 signing key: HMAC chain date -> region -> service -> aws4_request.</summary>
        public static byte[] DeriveSigningKey(string secretAccessKey, string dateStamp, string region)
        {
            var kSecret = Encoding.UTF8.GetBytes("AWS4" + secretAccessKey);
            var kDate = Hmac(kSecret, dateStamp);
            var kRegion = Hmac(kDate, region);
            var kService = Hmac(kRegion, Service);
            return Hmac(kService, Terminator);
        }

        public static string ComputeSignatureHex(byte[] signingKey, string stringToSign)
            => Convert.ToHexString(Hmac(signingKey, stringToSign)).ToLowerInvariant();

        /// <summary>
        /// RFC 3986 URI-encoding as SigV4 requires it: unreserved characters
        /// (A-Z a-z 0-9 - . _ ~) pass through, everything else becomes %XX with
        /// uppercase hex. When <paramref name="preserveSlash"/> is set (canonical URI
        /// path), '/' is left unencoded. Shared by the verifier and any signer so the
        /// two agree on the canonical form by construction, never by coincidence.
        /// </summary>
        public static string UriEncode(string value, bool preserveSlash)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                var c = (char)b;
                if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                    or '-' or '.' or '_' or '~'
                    || (preserveSlash && c == '/'))
                    sb.Append(c);
                else
                    sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Canonical URI: each path segment RFC 3986 URI-encoded with '/' preserved.
        /// An empty path canonicalizes to "/".
        /// </summary>
        public static string CanonicalUri(string path)
            => string.IsNullOrEmpty(path) ? "/" : UriEncode(path, preserveSlash: true);

        /// <summary>
        /// Canonical query string: every parameter URI-encoded, then sorted by
        /// encoded name (ties broken by encoded value), joined with '&amp;'. The caller
        /// removes X-Amz-Signature before calling for a presigned request.
        /// </summary>
        public static string CanonicalQueryString(IEnumerable<KeyValuePair<string, string>> parameters)
        {
            var encoded = parameters
                .Select(p => (Name: UriEncode(p.Key, false), Value: UriEncode(p.Value ?? string.Empty, false)))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ThenBy(p => p.Value, StringComparer.Ordinal);
            return string.Join('&', encoded.Select(p => $"{p.Name}={p.Value}"));
        }

        private static byte[] Hmac(byte[] key, string data)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }
    }
}
