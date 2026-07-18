using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// The bound context an OData server-driven-paging continuation token is issued
    /// against and MUST be re-validated against on replay. Every field participates in
    /// the token's MAC, so a token minted for one
    /// (entity set, query shape, page size, identity) shape is rejected the instant ANY
    /// of them differs on the continuation request — a token minted for set A replayed on
    /// set B, minted for one <c>$filter</c>/<c>$orderby</c> replayed with a different one,
    /// minted at one page size replayed at another, or minted by identity A replayed by
    /// identity B, all recompute a different MAC over the transmitted position and fail
    /// closed. None of these fields is transmitted: each is re-derived from the LIVE
    /// request and the MAC recomputed (the QBE saved-query-drift lesson — derive gates
    /// from live state, don't trust a persisted fingerprint).
    /// </summary>
    public readonly record struct ODataPageBinding(
        string EntitySet,
        string QueryShapeHash,
        int PageSize,
        string IdentityFingerprint);

    /// <summary>
    /// Issues and validates the opaque, integrity-protected continuation token that
    /// backs <c>@odata.nextLink</c> (carried on the wire as <c>$skiptoken</c>).
    ///
    /// <para><b>The token carries POSITION ONLY</b> — the next page's row offset and the
    /// issue time — never scope, tenant, policy, or any row/PK data (criterion 4: an
    /// offset integer is not readable row content a caller could not otherwise derive).
    /// Containment of a forged or replayed token does not come from the token validating
    /// the caller's scope; it comes from the read pipeline ANDing tenant/soft-delete/policy
    /// filters onto every query unconditionally through <see cref="Core.Resolvers.IQueryIntentExecutor"/>,
    /// so a token pointing anywhere still resolves to at most the caller's own visible rows.
    /// The MAC is the tamper/replay guard, not the authorization boundary. (Same shape as
    /// <see cref="S3.S3ContinuationToken"/>.)</para>
    ///
    /// <para><b>Wire form:</b> <c>base64url(offset '|' issuedAtUnix) '.' base64url(mac)</c>.
    /// The MAC is HMAC-SHA256 over a canonical, length-prefixed serialization of the
    /// binding PLUS the position, so nothing about the binding is transmitted. A
    /// malformed, tampered, expired, or cross-context token throws
    /// <see cref="ODataProtocolException.BadRequest"/> — in the middleware's caught family
    /// (never a silent "start from the top", never an unhandled 500).</para>
    /// </summary>
    public static class ODataContinuationToken
    {
        // Bump when the canonical layout changes so old tokens fail closed rather than
        // being reinterpreted under a new schema.
        private const string Version = "odatact1";

        /// <summary>Mints the token that resumes at <paramref name="offset"/>.</summary>
        public static string Issue(int offset, DateTimeOffset issuedAt, ODataPageBinding binding, byte[] secret)
        {
            ArgumentNullException.ThrowIfNull(secret);
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));

            var payload = Payload(offset, issuedAt.ToUnixTimeSeconds());
            var mac = ComputeMac(payload, binding, secret);
            return Base64Url(Encoding.UTF8.GetBytes(payload)) + "." + Base64Url(mac);
        }

        /// <summary>
        /// Validates <paramref name="token"/> against <paramref name="binding"/> re-derived
        /// from the LIVE request and returns the resume offset it encodes. Any malformed,
        /// tampered, cross-context, or expired token throws
        /// <see cref="ODataProtocolException.BadRequest"/> with a generic message — the token
        /// internals are never echoed (invariant 3).
        /// </summary>
        public static int Decode(
            string token, ODataPageBinding binding, byte[] secret, DateTimeOffset now, TimeSpan ttl)
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
                payloadBytes = FromBase64Url(token[..dot]);
                presentedMac = FromBase64Url(token[(dot + 1)..]);
            }
            // Decoding untrusted wire text catches the full parse-exception family, not just
            // FormatException — a truncated/over-length base64url segment must become a clean
            // 400, never an unhandled fault (invariant 5).
            catch (Exception ex) when (ex is FormatException or ArgumentException or DecoderFallbackException)
            {
                throw InvalidToken();
            }

            string payload;
            try
            {
                payload = Encoding.UTF8.GetString(payloadBytes);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or DecoderFallbackException)
            {
                throw InvalidToken();
            }

            var expectedMac = ComputeMac(payload, binding, secret);

            // Unconditional constant-time compare over the recomputed MAC — run it BEFORE any
            // structural parse of the payload so the integrity gate is not short-circuited by a
            // decode fault (invariant 2). Because the binding is folded into the MAC and
            // re-derived from the CURRENT request, a token replayed against a different set,
            // query shape, page size, or caller can never match.
            var macOk = CryptographicOperations.FixedTimeEquals(presentedMac, expectedMac);

            // Parse the position only after the MAC decision is computed. A well-formed but
            // out-of-range offset (e.g. a 29-digit number) throws OverflowException from
            // int.Parse — caught here into the same clean 400 (invariant 5).
            var parsed = TryParsePayload(payload, out var offset, out var issuedAtUnix);

            if (!macOk || !parsed)
                throw InvalidToken();

            // Expiry: an authentic-but-stale token fails closed exactly like a forged one — the
            // same generic error, so there is no oracle distinguishing them.
            var age = now - DateTimeOffset.FromUnixTimeSeconds(issuedAtUnix);
            if (age < TimeSpan.Zero || age > ttl)
                throw InvalidToken();

            return offset;
        }

        /// <summary>
        /// The stable identity fingerprint bound into a token so it cannot be replayed by a
        /// different principal. Built from the scalar and string-sequence entries of the user
        /// context (user id, roles, tenant, …) — the same values the read pipeline scopes on —
        /// hashed, so no identity plaintext ever reaches the wire. Opaque/complex entries are
        /// excluded so the fingerprint is deterministic across requests by the same principal.
        /// </summary>
        public static string FingerprintIdentity(IDictionary<string, object?> userContext)
        {
            ArgumentNullException.ThrowIfNull(userContext);

            var parts = new List<string>();
            foreach (var kv in userContext)
            {
                var rendered = RenderClaim(kv.Value);
                if (rendered is not null)
                    parts.Add(kv.Key + "=" + rendered);
            }
            parts.Sort(StringComparer.Ordinal);

            var canonical = string.Join("\n", parts);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }

        /// <summary>
        /// The stable hash of the caller-visible query shape ($filter/$select/$orderby). Bound
        /// into the token so a token minted for one shape replayed with a different one fails
        /// the MAC check (cross-query protection, criterion 3). The raw option text is
        /// length-prefixed so two distinct shapes can never render to the same bytes.
        /// </summary>
        public static string QueryShapeHash(string? filter, string? select, string? orderBy)
        {
            var canonical = new StringBuilder()
                .Append(Field(filter))
                .Append(Field(select))
                .Append(Field(orderBy))
                .ToString();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

            static string Field(string? v)
            {
                v ??= string.Empty;
                return v.Length.ToString(CultureInfo.InvariantCulture) + ":" + v + "\n";
            }
        }

        private static string Payload(int offset, long issuedAtUnix)
            => offset.ToString(CultureInfo.InvariantCulture) + "|" + issuedAtUnix.ToString(CultureInfo.InvariantCulture);

        private static bool TryParsePayload(string payload, out int offset, out long issuedAtUnix)
        {
            offset = 0;
            issuedAtUnix = 0;
            var bar = payload.IndexOf('|');
            if (bar <= 0 || bar == payload.Length - 1)
                return false;
            return int.TryParse(payload[..bar], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out offset)
                && offset >= 0
                && long.TryParse(payload[(bar + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out issuedAtUnix);
        }

        private static byte[] ComputeMac(string payload, ODataPageBinding binding, byte[] secret)
        {
            // Length-prefixing the variable fields makes the canonical serialization injective:
            // two distinct bindings can never render to the same byte string.
            var canonical = new StringBuilder()
                .Append(Version).Append('\n')
                .Append(Prefixed(binding.EntitySet)).Append('\n')
                .Append(Prefixed(binding.QueryShapeHash)).Append('\n')
                .Append(binding.PageSize).Append('\n')
                .Append(Prefixed(binding.IdentityFingerprint)).Append('\n')
                .Append(Prefixed(payload))
                .ToString();

            return HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical));

            static string Prefixed(string v)
                => v.Length.ToString(CultureInfo.InvariantCulture) + ":" + v;
        }

        private static ODataProtocolException InvalidToken()
            => ODataProtocolException.BadRequest("The continuation token ($skiptoken) provided is not valid.");

        private static string? RenderClaim(object? value) => value switch
        {
            null => null,
            string s => s,
            bool or byte or short or int or long or Guid =>
                Convert.ToString(value, CultureInfo.InvariantCulture),
            IEnumerable<string> seq => "[" + string.Join(",", seq) + "]",
            IEnumerable e => "[" + string.Join(",", e.Cast<object?>().Select(x => x?.ToString() ?? "")) + "]",
            _ => null, // opaque/complex entries are not part of the stable identity shape
        };

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
