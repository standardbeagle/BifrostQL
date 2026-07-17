using System.Security.Cryptography;
using System.Text;

namespace BifrostQL.Server.S3
{
    /// <summary>
    /// The bound context an opaque continuation token is issued against and must be
    /// replayed against. Every field participates in the token's MAC, so a token
    /// issued for one (bucket, prefix, delimiter, page-size, identity) shape is
    /// rejected the moment ANY of them differs on replay — cross-bucket reuse, a
    /// tampered prefix, a widened page size, or a different caller all recompute a
    /// different MAC over the transmitted position and fail closed.
    /// </summary>
    public readonly record struct S3ListBinding(
        string Bucket,
        string Prefix,
        string Delimiter,
        int MaxKeys,
        string IdentityFingerprint);

    /// <summary>
    /// Issues and validates the opaque, integrity-protected continuation token that
    /// resumes a <c>ListObjectsV2</c> enumeration.
    ///
    /// <para><b>The token carries POSITION ONLY</b> — the last emitted item's sort
    /// key — never scope, tenant, or policy. Containment of a forged or replayed
    /// token does not come from the token validating the caller's scope; it comes
    /// from the read pipeline ANDing tenant/policy/soft-delete filters onto every
    /// query unconditionally (the <c>IQueryIntentExecutor</c> seam), so a token
    /// pointing anywhere still resolves to at most the caller's own visible objects.
    /// The MAC is the tamper/replay guard, not the authorization boundary. (Same
    /// shape as the RESP SCAN cursor — see
    /// docs/solutions/bifrostql/resp-slice4-keyset-pagination-2026-07-14.md.)</para>
    ///
    /// <para><b>Wire form:</b> <c>base64url(positionUtf8) '.' base64url(mac)</c>. The
    /// position is not secret — it is an object key the caller has already been shown
    /// — so it need not be encrypted, only bound. The MAC is HMAC-SHA256 over a
    /// canonical serialization of the binding PLUS the position, so nothing about the
    /// binding is transmitted: it is re-derived from the current request on replay
    /// and the MAC recomputed. A mismatch is an <see cref="S3ProtocolException"/> in
    /// the middleware's caught family (never an escape to the host).</para>
    /// </summary>
    public static class S3ContinuationToken
    {
        // Bump when the canonical layout changes so old tokens fail closed rather
        // than being reinterpreted under a new schema.
        private const string Version = "s3ct1";

        /// <summary>Mints the token that resumes strictly after <paramref name="position"/>.</summary>
        public static string Issue(string position, S3ListBinding binding, byte[] secret)
        {
            ArgumentNullException.ThrowIfNull(position);
            ArgumentNullException.ThrowIfNull(secret);

            var mac = ComputeMac(position, binding, secret);
            return Base64Url(Encoding.UTF8.GetBytes(position)) + "." + Base64Url(mac);
        }

        /// <summary>
        /// Validates <paramref name="token"/> against <paramref name="binding"/> and
        /// returns the resume position it encodes. Any malformed, tampered, or
        /// cross-binding (e.g. cross-bucket) token throws
        /// <see cref="S3ProtocolException"/> — never a silent "start from the top",
        /// which would leak the first page to a caller replaying a stale/forged token.
        /// </summary>
        public static string Decode(string token, S3ListBinding binding, byte[] secret)
        {
            ArgumentNullException.ThrowIfNull(secret);

            if (string.IsNullOrEmpty(token))
                throw InvalidToken();

            var dot = token.IndexOf('.');
            if (dot <= 0 || dot == token.Length - 1)
                throw InvalidToken();

            byte[] positionBytes, presentedMac;
            try
            {
                positionBytes = FromBase64Url(token[..dot]);
                presentedMac = FromBase64Url(token[(dot + 1)..]);
            }
            catch (FormatException)
            {
                throw InvalidToken();
            }

            var position = Encoding.UTF8.GetString(positionBytes);
            var expectedMac = ComputeMac(position, binding, secret);

            // Unconditional constant-time compare over the recomputed MAC. Because the
            // binding (bucket/prefix/delimiter/max-keys/identity) is folded into the
            // MAC and re-derived from the CURRENT request, a token replayed against a
            // different bucket or a different caller can never match.
            if (!CryptographicOperations.FixedTimeEquals(presentedMac, expectedMac))
                throw InvalidToken();

            return position;
        }

        private static byte[] ComputeMac(string position, S3ListBinding binding, byte[] secret)
        {
            var canonical = new StringBuilder()
                .Append(Version).Append('\n')
                .Append(binding.Bucket).Append('\n')
                .Append(binding.Prefix.Length).Append(':').Append(binding.Prefix).Append('\n')
                .Append(binding.Delimiter.Length).Append(':').Append(binding.Delimiter).Append('\n')
                .Append(binding.MaxKeys).Append('\n')
                .Append(binding.IdentityFingerprint).Append('\n')
                .Append(position.Length).Append(':').Append(position)
                .ToString();

            return HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical));
        }

        // Length-prefixing the variable fields (prefix/delimiter/position) makes the
        // canonical serialization injective: two distinct (prefix, delimiter) pairs
        // can never render to the same byte string, so a delimiter cannot be smuggled
        // into a prefix to forge a matching MAC.
        private static S3ProtocolException InvalidToken()
            => S3ProtocolException.InvalidArgument("The continuation token provided is not valid.");

        private static string Base64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] FromBase64Url(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = (padded.Length % 4) switch
            {
                2 => padded + "==",
                3 => padded + "=",
                0 => padded,
                _ => throw new FormatException("Invalid base64url length."),
            };
            return Convert.FromBase64String(padded);
        }
    }
}
