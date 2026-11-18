using System.Globalization;
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
    /// key and the issue time — never scope, tenant, or policy. Containment of a
    /// forged or replayed token does not come from the token validating the caller's
    /// scope; it comes
    /// from the read pipeline ANDing tenant/policy/soft-delete filters onto every
    /// query unconditionally (the <c>IQueryIntentExecutor</c> seam), so a token
    /// pointing anywhere still resolves to at most the caller's own visible objects.
    /// The MAC is the tamper/replay guard, not the authorization boundary. (Same
    /// shape as the RESP SCAN cursor — see
    /// docs/solutions/bifrostql/resp-slice4-keyset-pagination-2026-07-14.md.)</para>
    ///
    /// <para><b>Expiry.</b> The issue time is inside the MAC, so it cannot be moved.
    /// <see cref="Decode"/> refuses a token older than the configured TTL, or issued more
    /// than <see cref="IssuerClockTolerance"/> in the future, with the same error as a
    /// forged one, so a captured token stops working after
    /// <see cref="S3Options.ContinuationTokenTtl"/> (mirrors
    /// <see cref="OData.ODataContinuationToken"/>).</para>
    ///
    /// <para><b>Wire form:</b> <c>base64url(issuedAtUnix '|' positionUtf8) '.' base64url(mac)</c>.
    /// The issue time is digits only and comes first, so the payload splits on its first
    /// <c>'|'</c> even when the position (a common prefix built from a client delimiter)
    /// contains one. The position is not secret — it is an object key the caller has
    /// already been shown — so it need not be encrypted, only bound. The MAC is
    /// HMAC-SHA256 over a canonical serialization of the binding PLUS the payload
    /// (issue time and position), so nothing about the
    /// binding is transmitted: it is re-derived from the current request on replay
    /// and the MAC recomputed. A mismatch is an <see cref="S3ProtocolException"/> in
    /// the middleware's caught family (never an escape to the host).</para>
    /// </summary>
    public static class S3ContinuationToken
    {
        // Bump when the canonical layout changes so old tokens fail closed rather
        // than being reinterpreted under a new schema.
        private const string Version = "s3ct2";

        // How far in the future an issue time may be: the skew between instances that share
        // ContinuationTokenSecret. Beyond it the token is refused like a forged one.
        internal static readonly TimeSpan IssuerClockTolerance = TimeSpan.FromMinutes(1);

        /// <summary>Mints the token that resumes strictly after <paramref name="position"/>.</summary>
        public static string Issue(string position, DateTimeOffset issuedAt, S3ListBinding binding, byte[] secret)
        {
            ArgumentNullException.ThrowIfNull(position);
            ArgumentNullException.ThrowIfNull(secret);

            var payload = issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "|" + position;
            var mac = ComputeMac(payload, binding, secret);
            return ProtocolBase64Url.Encode(Encoding.UTF8.GetBytes(payload)) + "." + ProtocolBase64Url.Encode(mac);
        }

        /// <summary>
        /// Validates <paramref name="token"/> against <paramref name="binding"/> and
        /// returns the resume position it encodes. Any malformed, tampered, expired
        /// (older than <paramref name="ttl"/> at <paramref name="now"/>), or
        /// cross-binding (e.g. cross-bucket) token throws
        /// <see cref="S3ProtocolException"/> — never a silent "start from the top",
        /// which would leak the first page to a caller replaying a stale/forged token.
        /// </summary>
        public static string Decode(
            string token, S3ListBinding binding, byte[] secret, DateTimeOffset now, TimeSpan ttl)
        {
            ArgumentNullException.ThrowIfNull(secret);

            if (string.IsNullOrEmpty(token))
                throw InvalidToken();

            var dot = token.IndexOf('.');
            if (dot <= 0 || dot == token.Length - 1)
                throw InvalidToken();

            byte[] payloadBytes, presentedMac;
            try
            {
                payloadBytes = ProtocolBase64Url.Decode(token[..dot]);
                presentedMac = ProtocolBase64Url.Decode(token[(dot + 1)..]);
            }
            catch (FormatException)
            {
                throw InvalidToken();
            }

            var payload = Encoding.UTF8.GetString(payloadBytes);
            var expectedMac = ComputeMac(payload, binding, secret);

            // Unconditional constant-time compare over the recomputed MAC. Because the
            // binding (bucket/prefix/delimiter/max-keys/identity) is folded into the
            // MAC and re-derived from the CURRENT request, a token replayed against a
            // different bucket or a different caller can never match.
            if (!CryptographicOperations.FixedTimeEquals(presentedMac, expectedMac))
                throw InvalidToken();

            // The payload is authentic, so only a token this server minted reaches here.
            // Invariant 5: the issue time parses as plain digits (NumberStyles.None) with invariant glyphs.
            var bar = payload.IndexOf('|');
            if (bar <= 0 || !long.TryParse(payload.AsSpan(0, bar), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var issuedAtUnix))
                throw InvalidToken();

            // An authentic but stale (or future-dated) token fails exactly like a forged one,
            // so the error is not an oracle for which check refused it. A token from slightly
            // ahead is accepted: with a shared secret, another instance's clock may run ahead.
            DateTimeOffset issuedAt;
            try
            {
                issuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedAtUnix);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw InvalidToken();
            }
            var age = now - issuedAt;
            if (age < -IssuerClockTolerance || age > ttl)
                throw InvalidToken();

            return payload[(bar + 1)..];
        }

        private static byte[] ComputeMac(string payload, S3ListBinding binding, byte[] secret)
        {
            var canonical = new StringBuilder()
                .Append(Version).Append('\n')
                .Append(binding.Bucket).Append('\n')
                .Append(binding.Prefix.Length).Append(':').Append(binding.Prefix).Append('\n')
                .Append(binding.Delimiter.Length).Append(':').Append(binding.Delimiter).Append('\n')
                .Append(binding.MaxKeys).Append('\n')
                .Append(binding.IdentityFingerprint).Append('\n')
                .Append(payload.Length).Append(':').Append(payload)
                .ToString();

            return HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical));
        }

        // Length-prefixing the variable fields (prefix/delimiter/position) makes the
        // canonical serialization injective: two distinct (prefix, delimiter) pairs
        // can never render to the same byte string, so a delimiter cannot be smuggled
        // into a prefix to forge a matching MAC.
        private static S3ProtocolException InvalidToken()
            => S3ProtocolException.InvalidArgument("The continuation token provided is not valid.");
    }
}
