using System.Buffers;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Storage
{
    /// <summary>
    /// Service for managing file storage operations with database integration.
    /// Handles upload, download, and metadata management for file columns.
    /// </summary>
    public sealed class FileStorageService
    {
        private readonly StorageProviderFactory _providerFactory;
        private readonly StorageBucketConfig? _databaseDefaultConfig;

        public FileStorageService(StorageProviderFactory? providerFactory = null, StorageBucketConfig? databaseDefaultConfig = null)
        {
            _providerFactory = providerFactory ?? new StorageProviderFactory();
            _databaseDefaultConfig = databaseDefaultConfig;
        }

        /// <summary>
        /// Gets the storage bucket configuration for a specific column.
        /// Resolves in order: column metadata > table metadata > database metadata
        /// </summary>
        public StorageBucketConfig? GetBucketConfig(IDbTable table, ColumnDto column, IDbModel model)
        {
            // Check column-level storage config
            var columnConfig = column.GetMetadataValue(MetadataKeys.Storage.Config);
            if (!string.IsNullOrWhiteSpace(columnConfig))
            {
                var config = StorageBucketConfig.FromMetadata(columnConfig);
                if (config != null)
                    return config;
            }

            // Check table-level storage config
            var tableConfig = table.GetMetadataValue(MetadataKeys.Storage.Config);
            if (!string.IsNullOrWhiteSpace(tableConfig))
            {
                var config = StorageBucketConfig.FromMetadata(tableConfig);
                if (config != null)
                    return config;
            }

            // Fall back to database-level config
            return _databaseDefaultConfig;
        }

        /// <summary>
        /// Checks if a column is configured as a file storage column
        /// </summary>
        public bool IsFileStorageColumn(IDbTable table, ColumnDto column, IDbModel model)
        {
            // Check for explicit file tag
            var fileTag = column.GetMetadataValue(MetadataKeys.FileStorage.File);
            if (!string.IsNullOrWhiteSpace(fileTag))
                return true;

            // Check if column has storage config (implies file storage)
            var hasStorageConfig = GetBucketConfig(table, column, model) != null;
            if (hasStorageConfig)
                return true;

            return false;
        }

        /// <summary>
        /// Gets the file storage configuration for a column
        /// </summary>
        public FileColumnConfig? GetFileColumnConfig(ColumnDto column)
        {
            var fileTag = column.GetMetadataValue(MetadataKeys.FileStorage.File);
            if (string.IsNullOrWhiteSpace(fileTag))
                return null;

            return FileColumnConfig.FromMetadata(fileTag);
        }

        /// <summary>
        /// Uploads a file and returns the file metadata to store in the database.
        /// </summary>
        /// <remarks>
        /// Residual limitation: <paramref name="content"/> arrives as a fully
        /// materialized <c>byte[]</c> because the GraphQL argument binder
        /// deserializes the whole upload before this method is ever called, so a
        /// large payload is already buffered in memory by the time any size check
        /// here can run. This method rejects as early as possible (before any
        /// storage I/O) to avoid compounding that with a second full copy on top
        /// of an oversized buffer, but it cannot prevent the initial buffering.
        /// Closing that gap fully would require switching the resolver's "file"
        /// argument to a stream/Content-Length-gated upload path.
        /// </remarks>
        /// <remarks>
        /// The storage key is always a fresh, non-deterministic
        /// <see cref="FileMetadata.GenerateFileKey"/> and is deliberately NOT
        /// caller-supplied. A caller that owns a deterministic address/row mapping
        /// (see <see cref="S3ObjectKeyMap"/>) must keep that address in the row's
        /// file pointer and let the bytes land on a fresh key: writing at the
        /// address overwrites the row's current content in place, before the
        /// mutation pipeline has authorized the write, so a denied write both
        /// destroys the victim's content and orphans the pointer. There is no
        /// parameter to reintroduce this — see the "address is not a storage key"
        /// rule in Storage/README.md.
        /// </remarks>
        /// <param name="customMetadata">Caller-supplied metadata persisted alongside the file pointer.</param>
        public async Task<FileMetadata> UploadFileAsync(
            IDbTable table,
            ColumnDto column,
            IDbModel model,
            string recordId,
            byte[] content,
            string? originalFileName = null,
            string? contentType = null,
            IReadOnlyDictionary<string, string>? customMetadata = null,
            CancellationToken cancellationToken = default)
        {
            var bucketConfig = GetBucketConfig(table, column, model)
                ?? throw new InvalidOperationException($"No storage configuration found for {table.DbName}.{column.ColumnName}");
            var columnConfig = GetFileColumnConfig(column);

            // Enforce the more restrictive of the bucket-level and column-level
            // max size, before any storage write (fix for size-cap-after-buffering
            // and for per-column limits never being enforced).
            var effectiveMaxSize = bucketConfig.MaxFileSize;
            if (columnConfig?.MaxFileSize is { } columnMaxSize && columnMaxSize < effectiveMaxSize)
                effectiveMaxSize = columnMaxSize;

            if (content.Length > effectiveMaxSize)
            {
                throw new InvalidOperationException(
                    $"File size ({content.Length} bytes) exceeds maximum allowed ({effectiveMaxSize} bytes)");
            }

            // Validate MIME type against the bucket's allow-list. A configured
            // allow-list with no client-supplied content type is rejected
            // (fail-closed) rather than silently bypassed, since the client hint
            // is otherwise spoofable and omission must not be a bypass.
            ValidateMimeType(bucketConfig.AllowedMimeTypes, contentType, "storage bucket");

            // Enforce the per-column accept list (fix: per-column config was
            // parsed but never checked), same fail-closed semantics.
            ValidateMimeType(columnConfig?.AcceptMimeTypes, contentType, "column");

            var provider = _providerFactory.GetProvider(bucketConfig);
            var effectiveFileKey = FileMetadata.GenerateFileKey(table.DbName, column.ColumnName, recordId, originalFileName);

            // Upload to storage
            var accessUrl = await provider.UploadAsync(bucketConfig, effectiveFileKey, content, contentType, cancellationToken);

            // Create and return metadata. BucketName/ProviderType are recorded
            // for informational/audit purposes only — they are never read back
            // as the storage target (see DownloadFileAsync/DeleteFileAsync/
            // GetFileUrlAsync, which always resolve the target bucket from the
            // column's configuration, not from this row-persisted value).
            var metadata = new FileMetadata
            {
                FileKey = effectiveFileKey,
                OriginalName = originalFileName,
                ContentType = contentType,
                Size = content.Length,
                BucketName = bucketConfig.BucketName,
                ProviderType = bucketConfig.ProviderType,
                UploadedAt = DateTime.UtcNow,
                AccessUrl = accessUrl,
                // Computed here, once, so every upload path (GraphQL resolver and
                // protocol adapters alike) persists the same ETag definition.
                ETag = System.Convert.ToHexString(
                    System.Security.Cryptography.MD5.HashData(content)).ToLowerInvariant(),
                CustomMetadata = customMetadata is { Count: > 0 }
                    ? new Dictionary<string, string>(customMetadata)
                    : null,
            };

            return metadata;
        }

        /// <summary>
        /// Validates <paramref name="contentType"/> against an allow-list. When
        /// <paramref name="allowedMimeTypes"/> is configured, a missing/empty
        /// content type is rejected rather than skipped, since an absent client
        /// hint must not bypass a configured allow-list.
        /// </summary>
        private static void ValidateMimeType(string[]? allowedMimeTypes, string? contentType, string scope)
        {
            if (allowedMimeTypes is not { Length: > 0 })
                return;

            if (string.IsNullOrEmpty(contentType))
            {
                throw new InvalidOperationException(
                    $"Content type is required by the {scope} configuration but was not provided.");
            }

            if (!allowedMimeTypes.Any(m =>
                m.Equals(contentType, StringComparison.OrdinalIgnoreCase) ||
                (m.EndsWith("/*") && contentType.StartsWith(m[..^1], StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException(
                    $"MIME type '{contentType}' is not allowed by the {scope} configuration. Allowed types: {string.Join(", ", allowedMimeTypes)}");
            }
        }

        /// <summary>
        /// Downloads a file by its metadata. The storage target (bucket/provider)
        /// is always resolved from the column's configuration via
        /// <see cref="GetBucketConfig"/> — never from <paramref name="metadata"/>'s
        /// row-persisted <c>BucketName</c>/<c>ProviderType</c>, which is an
        /// ordinary writable column value an attacker could set to point at an
        /// arbitrary bucket/provider. Only <see cref="FileMetadata.FileKey"/> (the
        /// object key within the configured bucket) is taken from the row, and it
        /// is still validated by the provider's normal key/traversal guards
        /// against that configured bucket.
        /// </summary>
        public async Task<byte[]> DownloadFileAsync(
            IDbTable table,
            ColumnDto column,
            IDbModel model,
            FileMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            var bucketConfig = GetBucketConfig(table, column, model)
                ?? throw new InvalidOperationException($"No storage configuration found for {table.DbName}.{column.ColumnName}");

            var provider = _providerFactory.GetProvider(bucketConfig);
            return await provider.DownloadAsync(bucketConfig, metadata.FileKey, cancellationToken);
        }

        /// <summary>
        /// Streams <paramref name="count"/> bytes starting at <paramref name="offset"/>
        /// from the stored object into <paramref name="destination"/>, without
        /// materializing the whole object in memory. This is the range/partial read
        /// path the S3 front door serves GetObject through: a full read is just
        /// <c>offset=0, count=Size</c>, still streamed rather than buffered.
        ///
        /// <para>The storage target (bucket/provider) is resolved from the column's
        /// configuration exactly as <see cref="DownloadFileAsync"/> does — never from
        /// the row-persisted <c>BucketName</c>/<c>ProviderType</c>, which an attacker
        /// could repoint. A seekable provider stream is positioned with a seek; a
        /// forward-only one is advanced by reading and discarding the prefix. Neither
        /// over-reads past <paramref name="offset"/> + <paramref name="count"/>.</para>
        /// </summary>
        public async Task CopyRangeToAsync(
            IDbTable table,
            ColumnDto column,
            IDbModel model,
            FileMetadata metadata,
            Stream destination,
            long offset,
            long count,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            var bucketConfig = GetBucketConfig(table, column, model)
                ?? throw new InvalidOperationException($"No storage configuration found for {table.DbName}.{column.ColumnName}");

            var provider = _providerFactory.GetProvider(bucketConfig);

            await using var source = await provider.OpenReadAsync(bucketConfig, metadata.FileKey, cancellationToken);
            await AdvanceToOffsetAsync(source, offset, cancellationToken);
            await CopyExactAsync(source, destination, count, cancellationToken);
        }

        // Positions a source stream at 'offset'. A seekable stream seeks; a
        // forward-only one reads and discards exactly 'offset' bytes so it never
        // relies on a seek the stream cannot honor, and never reads past the offset.
        private static async Task AdvanceToOffsetAsync(Stream source, long offset, CancellationToken cancellationToken)
        {
            if (offset == 0)
                return;

            if (source.CanSeek)
            {
                source.Seek(offset, SeekOrigin.Begin);
                return;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var remaining = offset;
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                    if (read == 0)
                        break; // source shorter than the offset: nothing left to skip
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Copies at most 'count' bytes and never one more, so a range read cannot
        // over-read into the rest of the object. Stops early if the source ends
        // (a truncated blob) rather than spinning.
        private static async Task CopyExactAsync(Stream source, Stream destination, long count, CancellationToken cancellationToken)
        {
            if (count == 0)
                return;

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var remaining = count;
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                    if (read == 0)
                        break; // source ended early; copy what exists, never over-read
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Deletes a file by its metadata. See <see cref="DownloadFileAsync"/> for
        /// why the storage target always comes from the column's configuration,
        /// never from row-persisted metadata.
        /// </summary>
        public async Task DeleteFileAsync(
            IDbTable table,
            ColumnDto column,
            IDbModel model,
            FileMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            // Fail closed rather than silently skipping: a missing storage
            // configuration with a live file pointer would otherwise let the
            // caller clear the DB record while orphaning the underlying object.
            var bucketConfig = GetBucketConfig(table, column, model)
                ?? throw new InvalidOperationException($"No storage configuration found for {table.DbName}.{column.ColumnName}");

            var provider = _providerFactory.GetProvider(bucketConfig);
            await provider.DeleteAsync(bucketConfig, metadata.FileKey, cancellationToken);
        }

        /// <summary>
        /// Gets a presigned URL for temporary file access. See
        /// <see cref="DownloadFileAsync"/> for why the storage target always
        /// comes from the column's configuration, never from row-persisted
        /// metadata.
        /// </summary>
        public async Task<string> GetFileUrlAsync(
            IDbTable table,
            ColumnDto column,
            IDbModel model,
            FileMetadata metadata,
            int expirationMinutes = 15,
            CancellationToken cancellationToken = default)
        {
            var bucketConfig = GetBucketConfig(table, column, model)
                ?? throw new InvalidOperationException($"No storage configuration found for {table.DbName}.{column.ColumnName}");

            var provider = _providerFactory.GetProvider(bucketConfig);
            return await provider.GetPresignedUrlAsync(bucketConfig, metadata.FileKey, expirationMinutes, forUpload: false);
        }
    }
}
