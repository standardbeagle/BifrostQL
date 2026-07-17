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

        public Task<Stream> OpenReadAsync(
            StorageBucketConfig bucketConfig,
            string fileKey,
            CancellationToken cancellationToken = default)
        {
            var fullPath = GetFullFilePath(bucketConfig, fileKey);

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"File not found: {fileKey}", fullPath);
            }

            // A seekable, async read stream: a byte-range read seeks straight to the
            // offset rather than reading (and discarding) the skipped prefix, so a
            // partial read touches only the requested bytes and never buffers the
            // whole object in memory. FileShare.Read allows concurrent readers.
            Stream stream = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);
            return Task.FromResult(stream);
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
            // Local storage has no real signed-URL scheme. Returning the
            // absolute server filesystem path here would leak the server's
            // directory layout to any GraphQL client. Return an opaque
            // reference relative to the configured bucket instead; a real
            // deployment should serve local files through a dedicated
            // download/upload route keyed by this reference rather than by
            // exposing the disk path.
            ArgumentException.ThrowIfNullOrWhiteSpace(bucketConfig.BucketName);
            ArgumentException.ThrowIfNullOrWhiteSpace(fileKey);

            var relativeKey = bucketConfig.GetFullPath(fileKey);
            if (Path.IsPathRooted(relativeKey))
                throw new InvalidOperationException("File key must be relative to the storage bucket.");

            return Task.FromResult($"local://{relativeKey.Replace(Path.DirectorySeparatorChar, '/')}");
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
                var key = ToStorageKey(bucketConfig, directory);
                entries.Add(new FileFolderEntry
                {
                    Name = info.Name,
                    Key = key,
                    IsFolder = true,
                    LastModified = info.LastWriteTimeUtc,
                    // Never expose the absolute server filesystem path (leaks the
                    // server's directory layout to GraphQL clients). Return an
                    // opaque reference relative to the bucket instead, matching
                    // GetPresignedUrlAsync's local:// scheme.
                    Url = $"local://{key}",
                });
            }

            foreach (var file in Directory.EnumerateFiles(fullPath, "*", search))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                var key = ToStorageKey(bucketConfig, file);
                entries.Add(new FileFolderEntry
                {
                    Name = info.Name,
                    Key = key,
                    IsFolder = false,
                    Size = info.Length,
                    LastModified = info.LastWriteTimeUtc,
                    // See the folder branch above: relative reference only, no
                    // absolute server path.
                    Url = $"local://{key}",
                });
            }

            return Task.FromResult<IReadOnlyList<FileFolderEntry>>(
                entries.OrderBy(e => e.IsFolder ? 0 : 1).ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private static string GetFullFolderPath(StorageBucketConfig bucketConfig, string folderKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(bucketConfig.BucketName);

            var basePath = Path.GetFullPath(bucketConfig.BucketName);
            if (!string.IsNullOrWhiteSpace(folderKey))
                RejectTraversal(folderKey);

            var prefixedKey = string.IsNullOrWhiteSpace(folderKey)
                ? bucketConfig.PathPrefix ?? ""
                : bucketConfig.GetFullPath(folderKey);

            if (Path.IsPathRooted(prefixedKey))
                throw new InvalidOperationException("Folder key must be relative to the storage bucket.");

            var fullPath = Path.GetFullPath(Path.Combine(basePath, prefixedKey));
            // Guard against escaping the configured prefix, not just the bucket
            // root: when prefixes separate tenants inside one bucket, a '..' that
            // cancels the prefix would still land under basePath and read another
            // tenant's files.
            EnsureUnderBase(PrefixRoot(bucketConfig, basePath), fullPath);
            return fullPath;
        }

        private static string GetFullFilePath(StorageBucketConfig bucketConfig, string fileKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(bucketConfig.BucketName);
            ArgumentException.ThrowIfNullOrWhiteSpace(fileKey);

            var basePath = Path.GetFullPath(bucketConfig.BucketName);
            RejectTraversal(fileKey);
            var prefixedKey = bucketConfig.GetFullPath(fileKey);

            if (Path.IsPathRooted(prefixedKey))
                throw new InvalidOperationException("File key must be relative to the storage bucket.");

            var fullPath = Path.GetFullPath(Path.Combine(basePath, prefixedKey));
            EnsureUnderBase(PrefixRoot(bucketConfig, basePath), fullPath);
            return fullPath;
        }

        // Rejects any key containing a ".." path segment. A row-persisted FileKey or
        // a folder-template value with ".." can otherwise cancel the configured
        // PathPrefix and escape into a sibling tenant's prefix within the bucket.
        private static void RejectTraversal(string key)
        {
            // Split only on real platform separators so a literal backslash inside a
            // filename on POSIX is not misread as a traversal (matches the OS's own
            // path semantics that EnsureUnderBase enforces on the resolved path).
            foreach (var segment in key.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (segment == "..")
                    throw new InvalidOperationException(
                        "Storage key must not contain '..' path segments.");
            }
        }

        // The effective root a key must resolve under: the bucket base joined with
        // the configured PathPrefix (or the bucket base when no prefix is set).
        private static string PrefixRoot(StorageBucketConfig bucketConfig, string basePath)
        {
            var prefix = bucketConfig.PathPrefix?.Trim();
            if (string.IsNullOrWhiteSpace(prefix))
                return basePath;
            return Path.GetFullPath(Path.Combine(basePath, prefix));
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
