namespace BifrostQL.Core.Storage
{
    /// <summary>
    /// Configuration for a storage bucket used for file storage.
    /// Can be configured at database or table level via metadata.
    /// </summary>
    public sealed class StorageBucketConfig
    {
        /// <summary>
        /// The name of the bucket (S3 bucket name, local directory path, etc.)
        /// </summary>
        public string BucketName { get; set; } = null!;

        /// <summary>
        /// The storage provider type (e.g., "local", "s3", "azure")
        /// </summary>
        public string ProviderType { get; set; } = "local";

        /// <summary>
        /// Optional path prefix for files within the bucket
        /// </summary>
        public string? PathPrefix { get; set; }

        /// <summary>
        /// Region for cloud storage providers (e.g., "us-east-1")
        /// </summary>
        public string? Region { get; set; }

        /// <summary>
        /// Custom endpoint URL for S3-compatible services
        /// </summary>
        public string? EndpointUrl { get; set; }

        /// <summary>
        /// Whether to use path-style addressing for S3 (vs virtual-hosted style)
        /// </summary>
        public bool UsePathStyle { get; set; }

        /// <summary>
        /// Maximum file size in bytes (default 10MB)
        /// </summary>
        public long MaxFileSize { get; set; } = 10 * 1024 * 1024;

        /// <summary>
        /// Allowed MIME types (empty/null means all types allowed)
        /// </summary>
        public string[]? AllowedMimeTypes { get; set; }

        /// <summary>
        /// Parses bucket configuration from metadata value.
        /// Format: "bucket:name;provider:local;prefix:path;maxSize:10485760"
        /// </summary>
        public static StorageBucketConfig? FromMetadata(string? metadataValue)
        {
            if (string.IsNullOrWhiteSpace(metadataValue))
                return null;

            var config = new StorageBucketConfig();
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
                    case "bucket":
                    case "bucketname":
                        config.BucketName = value;
                        break;
                    case "provider":
                    case "providertype":
                        config.ProviderType = value.ToLowerInvariant();
                        break;
                    case "prefix":
                    case "pathprefix":
                        config.PathPrefix = value;
                        break;
                    case "region":
                        config.Region = value;
                        break;
                    case "endpoint":
                    case "endpointurl":
                        config.EndpointUrl = value;
                        break;
                    case "pathstyle":
                    case "usepathstyle":
                        config.UsePathStyle = bool.TryParse(value, out var ps) && ps;
                        break;
                    case "maxsize":
                    case "maxfilesize":
                        if (long.TryParse(value, out var maxSize))
                            config.MaxFileSize = maxSize;
                        break;
                    case "mimetypes":
                    case "allowedmimetypes":
                        config.AllowedMimeTypes = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(m => m.Trim())
                            .ToArray();
                        break;
                }
            }

            return string.IsNullOrWhiteSpace(config.BucketName) ? null : config;
        }

        /// <summary>
        /// Gets the full path for a file key, including any configured prefix
        /// </summary>
        public string GetFullPath(string fileKey)
        {
            if (string.IsNullOrWhiteSpace(PathPrefix))
                return fileKey;
            return $"{PathPrefix.TrimEnd('/')}/{fileKey}";
        }
    }
}
