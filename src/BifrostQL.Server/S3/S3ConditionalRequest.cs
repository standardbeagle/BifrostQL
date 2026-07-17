using System.Globalization;

namespace BifrostQL.Server.S3
{
    /// <summary>The verdict of the conditional-header checks for a GET/HEAD object request.</summary>
    internal enum S3PreconditionOutcome
    {
        /// <summary>All preconditions pass: serve the object (200/206).</summary>
        Proceed,

        /// <summary>An <c>If-None-Match</c>/<c>If-Modified-Since</c> says the client's copy is current: 304.</summary>
        NotModified,

        /// <summary>An <c>If-Match</c>/<c>If-Unmodified-Since</c> guard failed: 412.</summary>
        PreconditionFailed,
    }

    /// <summary>
    /// Evaluates the RFC 7232 conditional request headers for GetObject/HeadObject in
    /// the standard precedence order (If-Match, then If-Unmodified-Since, then
    /// If-None-Match, then If-Modified-Since), against the object's ETag and
    /// last-modified time.
    ///
    /// <para>Deterministic and self-contained: an unparseable date is ignored (not an
    /// error), date comparisons are at whole-second granularity (HTTP-date carries no
    /// sub-second precision), and an object with no persisted ETag simply matches no
    /// entity-tag while still honoring <c>*</c> (the object exists). No versioning or
    /// multipart semantics are implied — this only answers "is the client's copy still
    /// current / still the one it named".</para>
    /// </summary>
    internal static class S3ConditionalRequest
    {
        public static S3PreconditionOutcome Evaluate(
            string? ifMatch,
            string? ifNoneMatch,
            string? ifModifiedSince,
            string? ifUnmodifiedSince,
            string? etag,
            DateTime lastModified)
        {
            // Step 1: If-Match. When present it supersedes If-Unmodified-Since (RFC 7232 §6).
            if (!string.IsNullOrWhiteSpace(ifMatch))
            {
                if (!ETagMatches(ifMatch, etag))
                    return S3PreconditionOutcome.PreconditionFailed;
            }
            // Step 2: If-Unmodified-Since — only when If-Match is absent.
            else if (!string.IsNullOrWhiteSpace(ifUnmodifiedSince)
                     && TryParseHttpDate(ifUnmodifiedSince, out var unmodifiedSince)
                     && Seconds(lastModified) > unmodifiedSince.ToUnixTimeSeconds())
            {
                return S3PreconditionOutcome.PreconditionFailed;
            }

            // Step 3: If-None-Match. When present it supersedes If-Modified-Since.
            if (!string.IsNullOrWhiteSpace(ifNoneMatch))
            {
                if (ETagMatches(ifNoneMatch, etag))
                    return S3PreconditionOutcome.NotModified;
            }
            // Step 4: If-Modified-Since — only when If-None-Match is absent.
            else if (!string.IsNullOrWhiteSpace(ifModifiedSince)
                     && TryParseHttpDate(ifModifiedSince, out var modifiedSince)
                     && Seconds(lastModified) <= modifiedSince.ToUnixTimeSeconds())
            {
                return S3PreconditionOutcome.NotModified;
            }

            return S3PreconditionOutcome.Proceed;
        }

        // Matches a comma-separated entity-tag list against the object's ETag. '*'
        // matches because the object exists at this point; a listed tag matches its
        // opaque value after stripping an optional weak marker and the quotes. An
        // object with no persisted ETag matches no listed tag (but still matches '*').
        private static bool ETagMatches(string headerValue, string? etag)
        {
            foreach (var raw in headerValue.Split(','))
            {
                var token = raw.Trim();
                if (token.Length == 0)
                    continue;
                if (token == "*")
                    return true;
                if (etag is not null && NormalizeTag(token) == etag)
                    return true;
            }
            return false;
        }

        private static string NormalizeTag(string token)
        {
            if (token.StartsWith("W/", StringComparison.Ordinal))
                token = token[2..];
            return token.Trim('"');
        }

        private static bool TryParseHttpDate(string value, out DateTimeOffset when)
            => DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when);

        // Whole-second Unix time: HTTP-date has no sub-second component, so the object's
        // last-modified must be compared truncated to seconds or a sub-second upload
        // would spuriously read as "modified after" an equal-second header value.
        private static long Seconds(DateTime lastModified)
            => new DateTimeOffset(DateTime.SpecifyKind(lastModified, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }
}
