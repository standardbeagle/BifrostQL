namespace BifrostQL.Core.Storage
{
    /// <summary>
    /// Configuration for a file storage column, parsed from the "file" metadata tag.
    /// Format: "type:image;maxSize:5242880;accept:image/*,image/png"
    /// </summary>
    public sealed class FileColumnConfig
    {
        /// <summary>
        /// File type category (image, document, video, audio, etc.)
        /// </summary>
        public string? FileType { get; set; }

        /// <summary>
        /// Maximum file size in bytes
        /// </summary>
        public long? MaxFileSize { get; set; }

        /// <summary>
        /// Accepted MIME types (comma-separated in metadata)
        /// </summary>
        public string[]? AcceptMimeTypes { get; set; }

        /// <summary>
        /// Whether to generate thumbnails for images
        /// </summary>
        public bool GenerateThumbnails { get; set; }

        /// <summary>
        /// Thumbnail sizes to generate (e.g., "100x100,300x300")
        /// </summary>
        public string[]? ThumbnailSizes { get; set; }

        /// <summary>
        /// Whether the file is publicly accessible without authentication
        /// </summary>
        public bool PublicAccess { get; set; }

        /// <summary>
        /// Custom storage path override (relative to bucket)
        /// </summary>
        public string? CustomPath { get; set; }

        /// <summary>
        /// Parses file column configuration from metadata value
        /// </summary>
        public static FileColumnConfig? FromMetadata(string? metadataValue)
        {
            if (string.IsNullOrWhiteSpace(metadataValue))
                return null;

            var config = new FileColumnConfig();
            var parts = metadataValue.Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var kv = part.Split(':', 2);
                if (kv.Length != 2)
                    continue;

                var key = kv[0].Trim().ToLowerInvariant();
                var value = kv[1].Trim();

                switch (key)
                {
                    case "type":
                    case "filetype":
                        config.FileType = value.ToLowerInvariant();
                        break;
                    case "maxsize":
                    case "maxfilesize":
                        if (long.TryParse(value, out var maxSize))
                            config.MaxFileSize = maxSize;
                        break;
                    case "accept":
                    case "acceptmimetypes":
                        config.AcceptMimeTypes = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(m => m.Trim())
                            .ToArray();
                        break;
                    case "thumbnails":
                    case "generatethumbnails":
                        config.GenerateThumbnails = bool.TryParse(value, out var genThumb) && genThumb;
                        break;
                    case "sizes":
                    case "thumbnailsizes":
                        config.ThumbnailSizes = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .ToArray();
                        break;
                    case "public":
                    case "publicaccess":
                        config.PublicAccess = bool.TryParse(value, out var pubAccess) && pubAccess;
                        break;
                    case "path":
                    case "custompath":
                        config.CustomPath = value;
                        break;
                }
            }

            return config;
        }

        /// <summary>
        /// Gets the HTML accept attribute value for file inputs
        /// </summary>
        public string? GetAcceptAttribute()
        {
            if (AcceptMimeTypes?.Length > 0)
                return string.Join(",", AcceptMimeTypes);

            return FileType?.ToLowerInvariant() switch
            {
                "image" or "img" or "picture" => "image/*",
                "document" or "doc" => ".pdf,.doc,.docx,.txt,.rtf",
                "video" => "video/*",
                "audio" => "audio/*",
                "archive" or "zip" => ".zip,.rar,.7z,.tar,.gz",
                _ => null
            };
        }
    }
}
