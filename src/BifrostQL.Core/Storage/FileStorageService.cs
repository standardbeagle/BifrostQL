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
            var columnConfig = column.GetMetadataValue("storage");
            if (!string.IsNullOrWhiteSpace(columnConfig))
            {
                var config = StorageBucketConfig.FromMetadata(columnConfig);
                if (config != null)
                    return config;
            }

            // Check table-level storage config
            var tableConfig = table.GetMetadataValue("storage");
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
            var fileTag = column.GetMetadataValue("file");
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
            var fileTag = column.GetMetadataValue("file");
            if (string.IsNullOrWhiteSpace(fileTag))
                return null;

            return FileColumnConfig.FromMetadata(fileTag);
        }

        /// <summary>
        /// Uploads a file and returns the file metadata to store in the database
        /// </summary>
        public async Task<FileMetadata> UploadFileAsync(
            IDbTable table,
            ColumnDto column,
            IDbModel model,
            string recordId,
            byte[] content,
            string? originalFileName = null,
            string? contentType = null,
            CancellationToken cancellationToken = default)
        {
            var bucketConfig = GetBucketConfig(table, column, model)
                ?? throw new InvalidOperationException($"No storage configuration found for {table.DbName}.{column.ColumnName}");

            // Validate file size
            if (content.Length > bucketConfig.MaxFileSize)
            {
                throw new InvalidOperationException(
                    $"File size ({content.Length} bytes) exceeds maximum allowed ({bucketConfig.MaxFileSize} bytes)");
            }

            // Validate MIME type
            if (bucketConfig.AllowedMimeTypes?.Length > 0 && !string.IsNullOrEmpty(contentType))
            {
                if (!bucketConfig.AllowedMimeTypes.Any(m => 
                    m.Equals(contentType, StringComparison.OrdinalIgnoreCase) ||
                    (m.EndsWith("/*") && contentType.StartsWith(m[..^1], StringComparison.OrdinalIgnoreCase))))
                {
                    throw new InvalidOperationException(
                        $"MIME type '{contentType}' is not allowed. Allowed types: {string.Join(", ", bucketConfig.AllowedMimeTypes)}");
                }
            }

            var provider = _providerFactory.GetProvider(bucketConfig);
            var fileKey = FileMetadata.GenerateFileKey(table.DbName, column.ColumnName, recordId, originalFileName);

            // Upload to storage
            var accessUrl = await provider.UploadAsync(bucketConfig, fileKey, content, contentType, cancellationToken);

            // Create and return metadata
            var metadata = new FileMetadata
            {
                FileKey = fileKey,
                OriginalName = originalFileName,
                ContentType = contentType,
                Size = content.Length,
                BucketName = bucketConfig.BucketName,
                ProviderType = bucketConfig.ProviderType,
                UploadedAt = DateTime.UtcNow,
                AccessUrl = accessUrl
            };

            return metadata;
        }

        /// <summary>
        /// Downloads a file by its metadata
        /// </summary>
        public async Task<byte[]> DownloadFileAsync(
            FileMetadata metadata,
            IDbModel model,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(metadata.ProviderType) || string.IsNullOrWhiteSpace(metadata.BucketName))
            {
                throw new InvalidOperationException("File metadata is missing provider or bucket information");
            }

            var bucketConfig = new StorageBucketConfig
            {
                BucketName = metadata.BucketName,
                ProviderType = metadata.ProviderType
            };

            var provider = _providerFactory.GetProvider(bucketConfig);
            return await provider.DownloadAsync(bucketConfig, metadata.FileKey, cancellationToken);
        }

        /// <summary>
        /// Deletes a file by its metadata
        /// </summary>
        public async Task DeleteFileAsync(
            FileMetadata metadata,
            IDbModel model,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(metadata.ProviderType) || string.IsNullOrWhiteSpace(metadata.BucketName))
            {
                return; // Nothing to delete
            }

            var bucketConfig = new StorageBucketConfig
            {
                BucketName = metadata.BucketName,
                ProviderType = metadata.ProviderType
            };

            var provider = _providerFactory.GetProvider(bucketConfig);
            await provider.DeleteAsync(bucketConfig, metadata.FileKey, cancellationToken);
        }

        /// <summary>
        /// Gets a presigned URL for temporary file access
        /// </summary>
        public async Task<string> GetFileUrlAsync(
            FileMetadata metadata,
            IDbModel model,
            int expirationMinutes = 15,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(metadata.ProviderType) || string.IsNullOrWhiteSpace(metadata.BucketName))
            {
                throw new InvalidOperationException("File metadata is missing provider or bucket information");
            }

            var bucketConfig = new StorageBucketConfig
            {
                BucketName = metadata.BucketName,
                ProviderType = metadata.ProviderType
            };

            var provider = _providerFactory.GetProvider(bucketConfig);
            return await provider.GetPresignedUrlAsync(bucketConfig, metadata.FileKey, expirationMinutes, forUpload: false);
        }
    }
}
