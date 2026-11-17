using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using BifrostQL.Core.Storage;

namespace BifrostQL.Aws;

public sealed class S3StorageProvider : IStorageProvider, IStorageFolderProvider
{
    private readonly Func<StorageBucketConfig, IAmazonS3> _clientFactory;

    public S3StorageProvider() : this(CreateClient)
    {
    }

    // The seam tests use to observe the requests a read sends without an S3 endpoint.
    internal S3StorageProvider(Func<StorageBucketConfig, IAmazonS3> clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public string ProviderType => "s3";

    public async Task<string> UploadAsync(
        StorageBucketConfig bucketConfig,
        string fileKey,
        byte[] content,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        var key = NormalizeKey(bucketConfig.GetFullPath(fileKey));
        using var client = _clientFactory(bucketConfig);
        await using var stream = new MemoryStream(content);

        var request = new PutObjectRequest
        {
            BucketName = bucketConfig.BucketName,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
        };

        await client.PutObjectAsync(request, cancellationToken);

        // Return a stable storage reference, never a presigned URL: a presigned
        // GET is a short-lived capability, and persisting it (as the upload path
        // once did) leaves a dead credential in the column JSON and every
        // history/CDC/audit copy (finding M25). Access URLs are computed on
        // read via GetPresignedUrlAsync with a clamped expiry.
        return $"s3://{bucketConfig.BucketName}/{key}";
    }

    public async Task<byte[]> DownloadAsync(
        StorageBucketConfig bucketConfig,
        string fileKey,
        CancellationToken cancellationToken = default)
    {
        var key = NormalizeKey(bucketConfig.GetFullPath(fileKey));
        using var client = _clientFactory(bucketConfig);
        using var response = await client.GetObjectAsync(bucketConfig.BucketName, key, cancellationToken);
        await using var output = new MemoryStream();
        await response.ResponseStream.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    /// <summary>
    /// Streams the object straight from the GET response; nothing is buffered. The returned
    /// stream owns the response and the client and disposes both.
    /// </summary>
    public Task<Stream> OpenReadAsync(
        StorageBucketConfig bucketConfig,
        string fileKey,
        CancellationToken cancellationToken = default)
        => OpenObjectStreamAsync(bucketConfig, fileKey, range: null, cancellationToken);

    /// <summary>
    /// Asks S3 for exactly the requested bytes with a <c>Range</c> header, so a range read
    /// transfers only those bytes instead of downloading the object. A zero-length read
    /// sends no request: a <c>Range</c> cannot express an empty range.
    /// </summary>
    public Task<Stream> OpenReadRangeAsync(
        StorageBucketConfig bucketConfig,
        string fileKey,
        long offset,
        long count,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
            return Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
        return OpenObjectStreamAsync(bucketConfig, fileKey, new ByteRange(offset, offset + count - 1), cancellationToken);
    }

    private async Task<Stream> OpenObjectStreamAsync(
        StorageBucketConfig bucketConfig, string fileKey, ByteRange? range, CancellationToken cancellationToken)
    {
        var key = NormalizeKey(bucketConfig.GetFullPath(fileKey));
        var client = _clientFactory(bucketConfig);
        try
        {
            var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucketConfig.BucketName,
                Key = key,
                ByteRange = range,
            }, cancellationToken);
            return new S3ObjectReadStream(response, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task DeleteAsync(
        StorageBucketConfig bucketConfig,
        string fileKey,
        CancellationToken cancellationToken = default)
    {
        var key = NormalizeKey(bucketConfig.GetFullPath(fileKey));
        using var client = _clientFactory(bucketConfig);
        await client.DeleteObjectAsync(bucketConfig.BucketName, key, cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        StorageBucketConfig bucketConfig,
        string fileKey,
        CancellationToken cancellationToken = default)
    {
        var key = NormalizeKey(bucketConfig.GetFullPath(fileKey));
        using var client = _clientFactory(bucketConfig);
        try
        {
            await client.GetObjectMetadataAsync(bucketConfig.BucketName, key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public Task<string> GetPresignedUrlAsync(
        StorageBucketConfig bucketConfig,
        string fileKey,
        int expirationMinutes = 15,
        bool forUpload = false)
    {
        var key = NormalizeKey(bucketConfig.GetFullPath(fileKey));
        using var client = _clientFactory(bucketConfig);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucketConfig.BucketName,
            Key = key,
            Verb = forUpload ? HttpVerb.PUT : HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(expirationMinutes),
        };

        return Task.FromResult(client.GetPreSignedURL(request));
    }

    public async Task<IReadOnlyList<FileFolderEntry>> ListFolderAsync(
        StorageBucketConfig bucketConfig,
        string folderKey,
        bool recursive = false,
        CancellationToken cancellationToken = default)
    {
        var prefix = NormalizeFolderPrefix(bucketConfig.GetFullPath(folderKey ?? ""));
        using var client = _clientFactory(bucketConfig);
        var entries = new List<FileFolderEntry>();
        string? continuationToken = null;

        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucketConfig.BucketName,
                Prefix = prefix,
                Delimiter = recursive ? null : "/",
                ContinuationToken = continuationToken,
            }, cancellationToken);

            foreach (var commonPrefix in response.CommonPrefixes)
            {
                entries.Add(new FileFolderEntry
                {
                    Name = LastPathSegment(commonPrefix.TrimEnd('/')),
                    Key = StripConfiguredPrefix(bucketConfig, commonPrefix.TrimEnd('/')),
                    IsFolder = true,
                    Url = $"s3://{bucketConfig.BucketName}/{commonPrefix}",
                });
            }

            foreach (var s3Object in response.S3Objects)
            {
                if (string.Equals(s3Object.Key, prefix, StringComparison.Ordinal))
                    continue;

                entries.Add(new FileFolderEntry
                {
                    Name = LastPathSegment(s3Object.Key),
                    Key = StripConfiguredPrefix(bucketConfig, s3Object.Key),
                    IsFolder = false,
                    Size = s3Object.Size,
                    LastModified = s3Object.LastModified,
                    Url = $"s3://{bucketConfig.BucketName}/{s3Object.Key}",
                });
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (!string.IsNullOrWhiteSpace(continuationToken));

        return entries
            .OrderBy(e => e.IsFolder ? 0 : 1)
            .ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IAmazonS3 CreateClient(StorageBucketConfig config)
    {
        var s3Config = new AmazonS3Config
        {
            ForcePathStyle = config.UsePathStyle,
        };

        if (!string.IsNullOrWhiteSpace(config.EndpointUrl))
            s3Config.ServiceURL = config.EndpointUrl;
        else if (!string.IsNullOrWhiteSpace(config.Region))
            s3Config.RegionEndpoint = RegionEndpoint.GetBySystemName(config.Region);

        return new AmazonS3Client(s3Config);
    }

    private static string NormalizeKey(string key)
    {
        var normalized = key.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("../", StringComparison.Ordinal)
            || normalized == ".."
            || normalized.EndsWith("/..", StringComparison.Ordinal))
            throw new InvalidOperationException("S3 key must not contain parent directory traversal.");
        return normalized;
    }

    private static string NormalizeFolderPrefix(string key)
    {
        var normalized = NormalizeKey(key);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";
        return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/";
    }

    private static string LastPathSegment(string key)
    {
        var trimmed = key.Trim('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static string StripConfiguredPrefix(StorageBucketConfig config, string key)
    {
        var prefix = config.PathPrefix?.Trim('/').Replace('\\', '/');
        return !string.IsNullOrWhiteSpace(prefix) && key.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
            ? key[(prefix.Length + 1)..]
            : key;
    }
}
