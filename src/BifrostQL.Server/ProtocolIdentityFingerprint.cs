using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BifrostQL.Server
{
    /// <summary>
    /// The one identity fingerprint every front door binds into its continuation tokens
    /// (gRPC page tokens, OData <c>$skiptoken</c>, S3 continuation tokens, RESP SCAN cursors,
    /// LDAP paged-results cookies) and keys its per-caller caches on (feed ETags, Prometheus
    /// scrape cache). Built from the scalar and sequence entries of the user context (user id,
    /// roles, tenant, …), the values the read pipeline scopes on, then hashed, so no identity
    /// plaintext reaches a wire or a cache key.
    ///
    /// <para>The canonical form is injective: every key, value and sequence element is
    /// length-prefixed, so a role list <c>["a","b"]</c>, a role list <c>["a,b"]</c> and the
    /// string <c>"[a,b]"</c> render differently. Opaque entries (the <c>ClaimsPrincipal</c>,
    /// arbitrary objects) are left out, so one principal's fingerprint is stable across
    /// requests.</para>
    /// </summary>
    internal static class ProtocolIdentityFingerprint
    {
        /// <summary>Hex SHA-256 of the canonical identity; null hashes as its own sentinel.</summary>
        public static string Of(IDictionary<string, object?>? userContext)
        {
            if (userContext is null)
                return Hash("\0anonymous");

            var parts = new List<string>(userContext.Count);
            foreach (var entry in userContext)
            {
                var rendered = RenderClaim(entry.Value);
                if (rendered is not null)
                    parts.Add(Prefixed(entry.Key) + "=" + rendered);
            }
            parts.Sort(StringComparer.Ordinal);
            return Hash(string.Join("\n", parts));
        }

        private static string? RenderClaim(object? value) => value switch
        {
            null => null,
            string s => "s" + Prefixed(s),
            bool or byte or short or int or long or Guid =>
                "v" + Prefixed(Convert.ToString(value, CultureInfo.InvariantCulture)!),
            IEnumerable sequence => "l" + RenderSequence(sequence),
            _ => null,
        };

        private static string RenderSequence(IEnumerable sequence)
        {
            var builder = new StringBuilder("[");
            foreach (var element in sequence)
                builder.Append(Prefixed(Convert.ToString(element, CultureInfo.InvariantCulture) ?? string.Empty));
            return builder.Append(']').ToString();
        }

        private static string Prefixed(string value)
            => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

        private static string Hash(string canonical)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>URL-safe unpadded base64 (RFC 4648 §5) shared by every continuation token.</summary>
    internal static class ProtocolBase64Url
    {
        public static string Encode(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>Decodes; throws <see cref="FormatException"/> on a malformed value.</summary>
        public static byte[] Decode(string value)
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
