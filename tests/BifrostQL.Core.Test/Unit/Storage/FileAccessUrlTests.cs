using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Test.Storage;

// Finding M25: the upload path persisted the provider-returned URL (for S3 a
// 15-minute presigned GET carrying X-Amz-Signature) as FileMetadata.AccessUrl
// inside the column JSON — a dead capability URL copied into history/CDC/audit.
// AccessUrl must be computed on read, never stored. Separately, the caller's
// expirationMinutes reached GetPresignedUrlAsync unclamped (int.MaxValue ->
// unmapped ArgumentOutOfRangeException inside DateTime.AddMinutes); it must
// clamp to the configured maximum (default 60) and reject non-positive values
// with BifrostExecutionError.
public class FileAccessUrlTests
{
    /// <summary>
    /// Mimics the pre-fix S3 provider: UploadAsync returns a presigned GET URL
    /// (a capability), and GetPresignedUrlAsync records the expiry it was
    /// asked for so the clamp is observable.
    /// </summary>
    private sealed class CapabilityUrlProvider : IStorageProvider
    {
        public string ProviderType => "s3like";
        public int? LastExpirationMinutes { get; private set; }

        public Task<string> UploadAsync(StorageBucketConfig bucketConfig, string fileKey, byte[] content, string? contentType = null, CancellationToken cancellationToken = default)
            => Task.FromResult($"https://s3.example/{bucketConfig.BucketName}/{fileKey}?X-Amz-Signature=deadbeef&X-Amz-Expires=900");

        public Task<byte[]> DownloadAsync(StorageBucketConfig bucketConfig, string fileKey, CancellationToken cancellationToken = default)
            => Task.FromResult(new byte[] { 1, 2, 3 });

        public Task DeleteAsync(StorageBucketConfig bucketConfig, string fileKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> ExistsAsync(StorageBucketConfig bucketConfig, string fileKey, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<string> GetPresignedUrlAsync(StorageBucketConfig bucketConfig, string fileKey, int expirationMinutes = 15, bool forUpload = false)
        {
            LastExpirationMinutes = expirationMinutes;
            return Task.FromResult($"https://s3.example/{bucketConfig.BucketName}/{fileKey}?X-Amz-Signature=read&X-Amz-Expires={expirationMinutes * 60}");
        }
    }

    private static (FileStorageService Service, IDbTable Table, ColumnDto Column, IDbModel Model, CapabilityUrlProvider Provider) Build(
        string? bucketMetadata = null)
    {
        var model = DbModelTestFixture.Create()
            .WithTable("docs", t => t
                .WithPrimaryKey("id", "int")
                .WithColumn("attachment"))
            .Build();
        var table = model.Tables.First(t => t.DbName == "docs");
        var column = table.ColumnLookup["attachment"];

        var provider = new CapabilityUrlProvider();
        var factory = new StorageProviderFactory();
        factory.RegisterProvider(provider);

        var bucketConfig = bucketMetadata != null
            ? StorageBucketConfig.FromMetadata($"bucket:b;provider:s3like;{bucketMetadata}")!
            : new StorageBucketConfig { BucketName = "b", ProviderType = "s3like" };

        return (new FileStorageService(factory, bucketConfig), table, column, model, provider);
    }

    [Fact]
    public async Task UploadFileAsync_StoredColumnJsonContainsNoPresignedUrl()
    {
        var (service, table, column, model, _) = Build();

        var metadata = await service.UploadFileAsync(
            table, column, model, "1", new byte[] { 1, 2, 3 }, "a.txt", "text/plain");

        var json = metadata.ToJson();
        Assert.DoesNotContain("X-Amz-Signature", json);
        // The capability field is never populated at all: the column JSON
        // stores the storage key (FileKey), not a URL.
        Assert.DoesNotContain("\"AccessUrl\":\"http", json);
        Assert.Contains(metadata.FileKey, json);
    }

    [Fact]
    public async Task UploadThenDownload_StillRoundTrips()
    {
        var (service, table, column, model, _) = Build();

        var metadata = await service.UploadFileAsync(
            table, column, model, "1", new byte[] { 1, 2, 3 }, "a.txt", "text/plain");
        var bytes = await service.DownloadFileAsync(table, column, model, metadata);

        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Theory]
    [InlineData(10080)]   // SigV4 7-day maximum — clamped to the 60-minute default
    [InlineData(int.MaxValue)] // pre-fix: ArgumentOutOfRangeException from DateTime.AddMinutes
    public async Task GetFileUrlAsync_ClampsExpiryToDefaultMaximum(int requested)
    {
        var (service, table, column, model, provider) = Build();
        var metadata = await service.UploadFileAsync(
            table, column, model, "1", new byte[] { 1 }, "a.txt", "text/plain");

        var url = await service.GetFileUrlAsync(table, column, model, metadata, requested);

        Assert.Equal(60, provider.LastExpirationMinutes);
        Assert.Contains(metadata.FileKey, url);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task GetFileUrlAsync_RejectsNonPositiveExpiry(int requested)
    {
        var (service, table, column, model, _) = Build();
        var metadata = await service.UploadFileAsync(
            table, column, model, "1", new byte[] { 1 }, "a.txt", "text/plain");

        await Assert.ThrowsAsync<BifrostExecutionError>(() =>
            service.GetFileUrlAsync(table, column, model, metadata, requested));
    }

    [Fact]
    public async Task GetFileUrlAsync_RespectsConfiguredMaximum()
    {
        var (service, table, column, model, provider) = Build("maxurlexpiry:10");
        var metadata = await service.UploadFileAsync(
            table, column, model, "1", new byte[] { 1 }, "a.txt", "text/plain");

        await service.GetFileUrlAsync(table, column, model, metadata, 60);

        Assert.Equal(10, provider.LastExpirationMinutes);
    }
}
