using System;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// Magic-byte content-type detection for media bytes. The media contract has
    /// no content-type column, so the type is derived from the bytes themselves at
    /// the moment the server holds them: the media fetch endpoint sniffs before
    /// streaming (falling back to <see cref="DefaultContentType"/>), and the vision
    /// path REQUIRES a recognized image type — only the four formats the Anthropic
    /// vision API accepts are recognized, so a sniff miss means the bytes cannot be
    /// vision input.
    /// </summary>
    public static class MediaContentSniffer
    {
        /// <summary>The fetch-endpoint fallback for bytes that are not a recognized image.</summary>
        public const string DefaultContentType = "application/octet-stream";

        /// <summary>
        /// Returns the image media type (<c>image/png</c>, <c>image/jpeg</c>,
        /// <c>image/gif</c>, <c>image/webp</c>) the content's magic bytes identify,
        /// or null when the content is not one of those formats.
        /// </summary>
        public static string? SniffImageMediaType(ReadOnlySpan<byte> content)
        {
            if (content.Length >= 8
                && content[..8].SequenceEqual(stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
                return "image/png";

            if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
                return "image/jpeg";

            if (content.Length >= 4
                && content[..4].SequenceEqual(stackalloc byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8' }))
                return "image/gif";

            if (content.Length >= 12
                && content[..4].SequenceEqual(stackalloc byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' })
                && content[8..12].SequenceEqual(stackalloc byte[] { (byte)'W', (byte)'E', (byte)'B', (byte)'P' }))
                return "image/webp";

            return null;
        }
    }
}
