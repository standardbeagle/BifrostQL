namespace BifrostQL.Core.Storage
{
    /// <summary>
    /// Interface for storage providers that handle file upload/download operations.
    /// Implementations can support local filesystem, S3, Azure Blob, etc.
    /// </summary>
    public interface IStorageProvider
    {
        /// <summary>
        /// The provider type identifier (e.g., "local", "s3", "azure")
        /// </summary>
        string ProviderType { get; }

        /// <summary>
        /// Uploads a file to storage
        /// </summary>
        /// <param name="bucketConfig">The bucket configuration</param>
        /// <param name="fileKey">Unique key/path for the file</param>
        /// <param name="content">File content as byte array</param>
        /// <param name="contentType">MIME type of the file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>URI or path to access the stored file</returns>
        Task<string> UploadAsync(
            StorageBucketConfig bucketConfig,
            string fileKey,
            byte[] content,
            string? contentType = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads a file from storage
        /// </summary>
        /// <param name="bucketConfig">The bucket configuration</param>
        /// <param name="fileKey">Unique key/path for the file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>File content as byte array</returns>
        Task<byte[]> DownloadAsync(
            StorageBucketConfig bucketConfig,
            string fileKey,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a file from storage
        /// </summary>
        /// <param name="bucketConfig">The bucket configuration</param>
        /// <param name="fileKey">Unique key/path for the file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        Task DeleteAsync(
            StorageBucketConfig bucketConfig,
            string fileKey,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks if a file exists in storage
        /// </summary>
        /// <param name="bucketConfig">The bucket configuration</param>
        /// <param name="fileKey">Unique key/path for the file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if file exists</returns>
        Task<bool> ExistsAsync(
            StorageBucketConfig bucketConfig,
            string fileKey,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a presigned URL for temporary access to a file
        /// </summary>
        /// <param name="bucketConfig">The bucket configuration</param>
        /// <param name="fileKey">Unique key/path for the file</param>
        /// <param name="expirationMinutes">URL expiration time in minutes</param>
        /// <param name="forUpload">If true, generates an upload URL; otherwise a download URL</param>
        /// <returns>Presigned URL</returns>
        Task<string> GetPresignedUrlAsync(
            StorageBucketConfig bucketConfig,
            string fileKey,
            int expirationMinutes = 15,
            bool forUpload = false);
    }
}
