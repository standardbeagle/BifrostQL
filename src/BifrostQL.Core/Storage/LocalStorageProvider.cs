namespace BifrostQL.Core.Storage
{
    /// <summary>
    /// Local filesystem storage provider for file storage.
    /// Stores files in a directory structure on the local filesystem.
    /// </summary>
    public sealed class LocalStorageProvider : IStorageProvider, IStorageFolderProvider
    {
        public string ProviderType => "local";

        public async Task<string> UploadAsync(
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

            await File.WriteAllBytesAsync(fullPath, content, cancellationToken);
            return fullPath;
        }

        public async Task<byte[]> DownloadAsync(
            StorageBucketConfig bucketConfig,
            string fileKey,
            CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullFilePath(bucketConfig, fileKey);

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"File not found: {fileKey}", fullPath);
            }

            return await File.ReadAllBytesAsync(fullPath, cancellationToken);
        }

        public Task DeleteAsync(
            StorageBucketConfig bucketConfig,
            string fileKey,
            CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullFilePath(bucketConfig, fileKey);

            // File.Delete is a metadata syscall with no async API; invoke directly.
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
            return Task.CompletedTask;
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

        public Task<IReadOnlyList<FileFolderEntry>> ListFolderAsync(
            StorageBucketConfig bucketConfig,
            string folderKey,
            bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullFolderPath(bucketConfig, folderKey);
            if (!Directory.Exists(fullPath))
                return Task.FromResult<IReadOnlyList<FileFolderEntry>>(Array.Empty<FileFolderEntry>());

            var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var entries = new List<FileFolderEntry>();

            foreach (var directory in Directory.EnumerateDirectories(fullPath, "*", search))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new DirectoryInfo(directory);
                entries.Add(new FileFolderEntry
                {
                    Name = info.Name,
                    Key = ToStorageKey(bucketConfig, directory),
                    IsFolder = true,
                    LastModified = info.LastWriteTimeUtc,
                    Url = $"file://{directory}",
                });
            }

            foreach (var file in Directory.EnumerateFiles(fullPath, "*", search))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                entries.Add(new FileFolderEntry
                {
                    Name = info.Name,
                    Key = ToStorageKey(bucketConfig, file),
                    IsFolder = false,
                    Size = info.Length,
                    LastModified = info.LastWriteTimeUtc,
                    Url = $"file://{file}",
                });
            }

            return Task.FromResult<IReadOnlyList<FileFolderEntry>>(
                entries.OrderBy(e => e.IsFolder ? 0 : 1).ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private static string GetFullFolderPath(StorageBucketConfig bucketConfig, string folderKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(bucketConfig.BucketName);

            var basePath = Path.GetFullPath(bucketConfig.BucketName);
            var prefixedKey = string.IsNullOrWhiteSpace(folderKey)
                ? bucketConfig.PathPrefix ?? ""
                : bucketConfig.GetFullPath(folderKey);

            if (Path.IsPathRooted(prefixedKey))
                throw new InvalidOperationException("Folder key must be relative to the storage bucket.");

            var fullPath = Path.GetFullPath(Path.Combine(basePath, prefixedKey));
            EnsureUnderBase(basePath, fullPath);
            return fullPath;
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
            EnsureUnderBase(basePath, fullPath);
            return fullPath;
        }

        private static void EnsureUnderBase(string basePath, string fullPath)
        {
            var normalizedBase = Path.EndsInDirectorySeparator(basePath)
                ? basePath
                : basePath + Path.DirectorySeparatorChar;

            if (!string.Equals(fullPath, basePath, StringComparison.OrdinalIgnoreCase)
                && !fullPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Storage key resolves outside the storage bucket.");
        }

        private static string ToStorageKey(StorageBucketConfig bucketConfig, string fullPath)
        {
            var basePath = Path.GetFullPath(bucketConfig.BucketName);
            var relative = Path.GetRelativePath(basePath, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            var prefix = bucketConfig.PathPrefix?.Trim('/').Replace('\\', '/');
            return !string.IsNullOrWhiteSpace(prefix) && relative.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
                ? relative[(prefix.Length + 1)..]
                : relative;
        }
    }
}
