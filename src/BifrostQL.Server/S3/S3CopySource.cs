using System;

namespace BifrostQL.Server.S3
{
    /// <summary>
    /// Parses the <c>x-amz-copy-source</c> header of a CopyObject request into the
    /// <c>(bucket, key)</c> coordinates of the source object, in the same encoding
    /// <see cref="BifrostQL.Core.Storage.S3ObjectKeyMap.ParseKey"/> consumes — so the
    /// decoded key is handed straight to the authorized read seam and never spliced
    /// into a path by hand.
    ///
    /// <para><b>Strict, single decode.</b> The header is URL-encoded on the wire by
    /// the S3 client, so it is decoded EXACTLY once to reveal the canonical
    /// <c>/bucket/key</c> reference; the key components keep their key-map
    /// percent-escaping for the seam to decode. A reference that is empty, rooted
    /// (more than one leading <c>/</c>), or carries a <c>.</c>/<c>..</c> traversal
    /// segment — literally, single-encoded, OR double-encoded (a second decode that
    /// surfaces a dot segment) — is rejected as <c>InvalidArgument</c> before the
    /// source is ever looked up. A query-qualified source (a <c>versionId</c>) is a
    /// non-goal and answers <c>NotImplemented</c>. This mirrors the slice-4 key-map
    /// traversal rejection rather than re-implementing path validation
    /// (.claude/rules/protocol-adapter-security.md invariant 5).</para>
    /// </summary>
    internal static class S3CopySource
    {
        public static (string Bucket, string Key) Parse(string? header)
        {
            if (string.IsNullOrEmpty(header))
                throw S3ProtocolException.InvalidArgument("x-amz-copy-source is required for a copy operation.");

            // Versioned / query-qualified copy sources are a non-goal.
            if (header.IndexOf('?') >= 0)
                throw S3ProtocolException.NotImplemented("Versioned or query-qualified copy sources are not supported.");

            // Exactly one optional leading slash separates the bucket; a second one is
            // a rooted/absolute reference and is rejected rather than normalized away.
            var reference = header[0] == '/' ? header[1..] : header;
            if (reference.Length == 0 || reference[0] == '/')
                throw S3ProtocolException.InvalidArgument("x-amz-copy-source is not a valid '/bucket/key' reference.");

            // Single decode undoes the S3-wire encoding layer. The key-map escaping of
            // the individual components survives for the seam's ParseKey to decode.
            var decoded = Uri.UnescapeDataString(reference);

            // Reject a dot-segment that is present literally or after this single decode,
            // AND one that only surfaces on a second decode (a double-encoded '..'). A
            // component may legitimately still carry key-map '%XX' escaping, so only a
            // decoded-out dot SEGMENT is rejected — not every residual escape.
            RejectTraversal(decoded);
            RejectTraversal(Uri.UnescapeDataString(decoded));

            if (decoded.Length == 0 || decoded[0] == '/')
                throw S3ProtocolException.InvalidArgument("x-amz-copy-source is not a valid '/bucket/key' reference.");

            var slash = decoded.IndexOf('/');
            if (slash <= 0 || slash == decoded.Length - 1)
                throw S3ProtocolException.InvalidArgument("x-amz-copy-source must be a '/bucket/key' reference.");

            return (decoded[..slash], decoded[(slash + 1)..]);
        }

        private static void RejectTraversal(string reference)
        {
            foreach (var segment in reference.Split('/'))
                if (segment is "." or "..")
                    throw S3ProtocolException.InvalidArgument(
                        "x-amz-copy-source must not contain path-traversal segments.");
        }
    }
}
