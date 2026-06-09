using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Test.Storage;

// Covers the offline, security-relevant surface of S3StorageProvider: the path
// traversal guard in NormalizeKey, which runs before any AWS client is created.
// Happy-path upload/download/list parsing talks to S3 and belongs in an
// integration test against a mock/localstack endpoint, not a unit test.
public class S3StorageProviderTests
{
    private readonly S3StorageProvider _provider = new();

    private static StorageBucketConfig Config(string? prefix = null) => new()
    {
        BucketName = "test-bucket",
        ProviderType = "s3",
        PathPrefix = prefix,
        Region = "us-east-1",
    };

    [Fact]
    public void ProviderType_ReturnsS3()
    {
        Assert.Equal("s3", _provider.ProviderType);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData("..")]
    [InlineData("nested/..")]
    [InlineData(@"..\outside.txt")]
    [InlineData(@"nested\..\..\outside.txt")]
    public async Task UploadAsync_WithTraversalKey_Throws(string fileKey)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.UploadAsync(Config(), fileKey, new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData(@"..\outside.txt")]
    public async Task DownloadAsync_WithTraversalKey_Throws(string fileKey)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.DownloadAsync(Config(), fileKey));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData(@"..\outside.txt")]
    public async Task DeleteAsync_WithTraversalKey_Throws(string fileKey)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.DeleteAsync(Config(), fileKey));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData(@"..\outside.txt")]
    public async Task ExistsAsync_WithTraversalKey_Throws(string fileKey)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.ExistsAsync(Config(), fileKey));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData(@"..\outside.txt")]
    public async Task GetPresignedUrlAsync_WithTraversalKey_Throws(string fileKey)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.GetPresignedUrlAsync(Config(), fileKey));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("nested/../../outside")]
    [InlineData(@"..\outside")]
    public async Task ListFolderAsync_WithTraversalKey_Throws(string folderKey)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.ListFolderAsync(Config(), folderKey));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(@"..\outside.txt")]
    public async Task UploadAsync_TraversalGuardAppliesToPrefixedPath(string fileKey)
    {
        // GetFullPath prepends the prefix, but the traversal segment in fileKey
        // must still be rejected.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.UploadAsync(Config(prefix: "uploads"), fileKey, new byte[] { 1 }));
    }
}
