using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BifrostQL.Server.Feeds
{
    /// <summary>The wire representation a feed request resolved to. The suffix/Accept negotiation lands here.</summary>
    public enum FeedFormat
    {
        /// <summary>RSS 2.0 (the default when nothing selects Atom).</summary>
        Rss,

        /// <summary>Atom 1.0.</summary>
        Atom,
    }

    /// <summary>
    /// The conditional-GET facts for a rendered feed: a validator <see cref="ETag"/> and a
    /// <see cref="LastModified"/> instant derived ONLY from the authorized, transformer-filtered result
    /// set (<see cref="FeedDocument"/>) plus the request representation (format + caller identity
    /// partition), and whether the request's <c>If-None-Match</c>/<c>If-Modified-Since</c> preconditions
    /// mean a 304 may be served.
    ///
    /// <para>Two security properties are structural here, not conventional
    /// (.claude/rules/protocol-adapter-security.md invariant 11 corollary i):</para>
    /// <list type="bullet">
    /// <item>The validator NEVER incorporates token/secret material — only the format, an identity
    /// PARTITION fingerprint (which is derived from the projected claims, never the raw feed token), and
    /// the document content. So two different tokens that map to the SAME principal and see the SAME
    /// filtered rows produce the SAME ETag, while two DIFFERENT principals never collide even on
    /// byte-identical content (the identity partition is folded in).</item>
    /// <item>An empty authorized result set uses a fixed deterministic <see cref="EmptyFeedLastModified"/>
    /// (Unix epoch — the same instant the Atom writer dates an empty feed from) and a stable ETag, so a
    /// caller polling an unchanged empty feed gets a deterministic 304 rather than a wall-clock value
    /// that defeats conditional GET.</item>
    /// </list>
    /// </summary>
    public sealed class FeedConditionalRequest
    {
        /// <summary>
        /// The deterministic <c>Last-Modified</c> for an empty feed. An empty result set has no item to
        /// date the feed from; a wall-clock fallback would make every poll differ and defeat caching, so
        /// the fallback is a fixed instant (mirrors <c>AtomFeedWriter.EmptyFeedUpdated</c>).
        /// </summary>
        public static readonly DateTime EmptyFeedLastModified = DateTime.UnixEpoch;

        private FeedConditionalRequest(string etag, DateTime lastModified, bool notModified)
        {
            ETag = etag;
            LastModified = lastModified;
            NotModified = notModified;
        }

        /// <summary>The strong entity tag, already quoted (e.g. <c>"a1b2…"</c>).</summary>
        public string ETag { get; }

        /// <summary>The UTC last-modified instant, truncated to whole seconds (HTTP-date resolution).</summary>
        public DateTime LastModified { get; }

        /// <summary>Whether the request's preconditions are satisfied and a 304 may be served.</summary>
        public bool NotModified { get; }

        /// <summary>
        /// Computes the validators for <paramref name="document"/> in <paramref name="format"/> for the
        /// caller identified by <paramref name="identityPartition"/>, and evaluates the request's
        /// conditional headers. <c>If-None-Match</c> takes precedence over <c>If-Modified-Since</c> (RFC
        /// 9110 §13.1.3): when an <c>If-None-Match</c> is present it alone decides, and the date header
        /// is consulted only in its absence.
        /// </summary>
        public static FeedConditionalRequest Evaluate(
            FeedDocument document,
            FeedFormat format,
            string identityPartition,
            string? ifNoneMatch,
            string? ifModifiedSince)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(identityPartition);

            var lastModified = TruncateToSeconds((document.Updated ?? EmptyFeedLastModified).ToUniversalTime());
            var etag = ComputeETag(document, format, identityPartition);

            bool notModified;
            if (!string.IsNullOrWhiteSpace(ifNoneMatch))
                notModified = IfNoneMatchSatisfied(ifNoneMatch, etag);
            else
                notModified = IfModifiedSinceSatisfied(ifModifiedSince, lastModified);

            return new FeedConditionalRequest(etag, lastModified, notModified);
        }

        /// <summary>
        /// A stable, token-free fingerprint of the caller's identity for cache/validator partitioning.
        /// Renders each identity-context entry (tenant id, subject, roles, …) into a canonical string
        /// and hashes it; opaque/complex entries such as the raw <see cref="System.Security.Claims.ClaimsPrincipal"/>
        /// stored under <c>"user"</c> are skipped, so nothing token-derived can enter the partition. The
        /// projected context is derived from principal claims only, so the same principal yields the same
        /// partition regardless of which token authenticated it.
        /// </summary>
        public static string IdentityPartition(IDictionary<string, object?> userContext)
        {
            ArgumentNullException.ThrowIfNull(userContext);

            var parts = new List<string>(userContext.Count);
            foreach (var kv in userContext)
            {
                var rendered = RenderValue(kv.Value);
                if (rendered is not null)
                    parts.Add(kv.Key + "=" + rendered);
            }
            parts.Sort(StringComparer.Ordinal);

            var canonical = string.Join("\n", parts);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }

        private static string ComputeETag(FeedDocument document, FeedFormat format, string identityPartition)
        {
            var canonical = new StringBuilder();
            // Representation dimensions: the wire format and the caller partition. Folding the partition
            // in means two DIFFERENT principals never share an ETag even on byte-identical content, while
            // it carries no token material so two tokens for the SAME principal DO share one.
            canonical.Append(format == FeedFormat.Atom ? "atom" : "rss").Append('');
            canonical.Append(identityPartition).Append('');
            canonical.Append(document.Items.Count.ToString(CultureInfo.InvariantCulture)).Append('');
            canonical.Append((document.Updated ?? EmptyFeedLastModified).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

            foreach (var item in document.Items)
            {
                canonical.Append('');
                canonical.Append(item.Guid).Append('');
                canonical.Append(item.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
            return "\"" + hash + "\"";
        }

        private static bool IfNoneMatchSatisfied(string ifNoneMatch, string etag)
        {
            // A wildcard matches any existing representation (RFC 9110 §13.1.2).
            if (ifNoneMatch.Trim() == "*")
                return true;

            foreach (var candidate in ifNoneMatch.Split(','))
            {
                var trimmed = candidate.Trim();
                // Weak validators compare equal to their strong form here; strip the W/ marker.
                if (trimmed.StartsWith("W/", StringComparison.Ordinal))
                    trimmed = trimmed[2..].Trim();
                if (string.Equals(trimmed, etag, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool IfModifiedSinceSatisfied(string? ifModifiedSince, DateTime lastModified)
        {
            if (string.IsNullOrWhiteSpace(ifModifiedSince))
                return false;

            // A malformed date is ignored (treated as absent), never a fault on the wire.
            if (!DateTimeOffset.TryParse(
                    ifModifiedSince, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var since))
                return false;

            // Not modified since the given instant → the cached copy is still fresh.
            return lastModified <= since.UtcDateTime;
        }

        private static DateTime TruncateToSeconds(DateTime value)
            => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

        private static string? RenderValue(object? value) => value switch
        {
            null => null,
            string s => s,
            bool or byte or short or int or long or Guid => Convert.ToString(value, CultureInfo.InvariantCulture),
            IEnumerable<string> seq => "[" + string.Join(",", seq) + "]",
            IEnumerable e => "[" + string.Join(",", e.Cast<object?>().Select(x => x?.ToString() ?? string.Empty)) + "]",
            _ => null, // opaque/complex entries (e.g. the ClaimsPrincipal) are never part of the stable partition
        };
    }
}
