namespace BifrostQL.Core.Storage
{
    /// <summary>
    /// Local filesystem storage provider for file storage.
    /// Stores files in a directory structure on the local filesystem.
    /// </summary>
    public sealed class LocalStorageProvider : IStorageProvider
    {
        public string ProviderType => "local";

        public Task<string> UploadAsync(
            StorageBucketConfig bucketConfig,
            string fileKey,
            byte[] content,
            string? contentType = null,
            CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullFilePath(bucketConfig, fileKey);
            var directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return Task.Run(() =>
            {
                File.WriteAllBytes(fullPath, content);
                return Task.FromResult(fullPath);
            }, cancellationToken);
        }

        public Task<byte[]> DownloadAsync(
            StorageBucketConfig bucketConfig,
            string fileKey,
            CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullFilePath(bucketConfig, fileKey);
            
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"File not found: {fileKey}", fullPath);
            }

            return Task.Run(() => File.ReadAllBytes(fullPath), cancellationToken);
        }

        public Task DeleteAsync(
            StorageBucketConfig bucketConfig,
            string fileKey,
            CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullFilePath(bucketConfig, fileKey);
            
            return Task.Run(() =>
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }, cancellationToken);
        }

        public Task<bool> ExistsAsync(
            StorageBucketConfig bucketConfig,
            string fileKey,
            CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullFilePath(bucketConfig, fileKey);
            return Task.FromResult(File.Exists(fullPath));
        }

        public Task<string> GetPresignedUrlAsync(
            StorageBucketConfig bucketConfig,
            string fileKey,
            int expirationMinutes = 15,
            bool forUpload = false)
        {
            // For local storage, return the file path directly
            // In production, this would typically be served through a file serving endpoint
            var fullPath = GetFullFilePath(bucketConfig, fileKey);
            return Task.FromResult($"file://{fullPath}");
        }

        private static string GetFullFilePath(StorageBucketConfig bucketConfig, string fileKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(bucketConfig.BucketName);
            ArgumentException.ThrowIfNullOrWhiteSpace(fileKey);

            var basePath = Path.GetFullPath(bucketConfig.BucketName);
            var prefixedKey = bucketConfig.GetFullPath(fileKey);

            if (Path.IsPathRooted(prefixedKey))
                throw new InvalidOperationException("File key must be relative to the storage bucket.");

            var fullPath = Path.GetFullPath(Path.Combine(basePath, prefixedKey));
            var normalizedBase = Path.EndsInDirectorySeparator(basePath)
                ? basePath
                : basePath + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("File key resolves outside the storage bucket.");

            return fullPath;
        }
    }
}
