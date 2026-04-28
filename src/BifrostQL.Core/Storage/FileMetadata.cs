namespace BifrostQL.Core.Storage
{
    /// <summary>
    /// Metadata for a stored file, stored in the database column.
    /// The column stores the file reference/path while actual file content is in storage.
    /// </summary>
    public sealed class FileMetadata
    {
        /// <summary>
        /// Unique identifier for the file (stored in the database column)
        /// </summary>
        public string FileKey { get; set; } = null!;

        /// <summary>
        /// Original filename when uploaded
        /// </summary>
        public string? OriginalName { get; set; }

        /// <summary>
        /// MIME type of the file
        /// </summary>
        public string? ContentType { get; set; }

        /// <summary>
        /// File size in bytes
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// Storage bucket name where the file is stored
        /// </summary>
        public string? BucketName { get; set; }

        /// <summary>
        /// Storage provider type used
        /// </summary>
        public string? ProviderType { get; set; }

        /// <summary>
        /// When the file was uploaded
        /// </summary>
        public DateTime UploadedAt { get; set; }

        /// <summary>
        /// URL or path to access the file
        /// </summary>
        public string? AccessUrl { get; set; }

        /// <summary>
        /// Additional metadata as key-value pairs
        /// </summary>
        public Dictionary<string, string>? CustomMetadata { get; set; }

        /// <summary>
        /// Serializes the file metadata to a JSON string for database storage
        /// </summary>
        public string ToJson()
        {
            return System.Text.Json.JsonSerializer.Serialize(this);
        }

        /// <summary>
        /// Deserializes file metadata from a JSON string
        /// </summary>
        public static FileMetadata? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<FileMetadata>(json);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Creates a simple file key from table, column, and record identifiers
        /// </summary>
        public static string GenerateFileKey(string tableName, string columnName, string recordId, string? originalFileName = null)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var random = Guid.NewGuid().ToString("N")[..8];
            var extension = string.IsNullOrEmpty(originalFileName) 
                ? "" 
                : Path.GetExtension(originalFileName);
            
            var safeTable = SanitizeForPath(tableName);
            var safeColumn = SanitizeForPath(columnName);
            var safeRecordId = SanitizeForPath(recordId);
            
            return $"{safeTable}/{safeColumn}/{safeRecordId}_{timestamp}_{random}{extension}";
        }

        private static string SanitizeForPath(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "_";

            // Include common problematic characters that may not be in InvalidFileNameChars on all platforms
            var invalid = Path.GetInvalidFileNameChars()
                .Concat(new[] { '/', '\\', ':', '<', '>', '|', '"', '*', '?' })
                .ToArray();
            var result = new System.Text.StringBuilder(input.Length);
            foreach (var c in input)
            {
                if (invalid.Contains(c))
                    result.Append('_');
                else
                    result.Append(c);
            }
            return result.ToString();
        }
    }
}
