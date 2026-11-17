using Amazon.S3;
using Amazon.S3.Model;
using BifrostQL.Core.Storage;
using NSubstitute;

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

    // A fake S3 serving a 4 KiB object: a ranged GET returns only the range's bytes, an
    // unranged one (either overload) the whole object. Every GetObjectRequest is kept.
    private sealed class FakeS3
    {
        public readonly byte[] Object = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        public readonly List<GetObjectRequest> Requests = new();
        public readonly IAmazonS3 Client = Substitute.For<IAmazonS3>();
        public long BodyBytesServed;

        public FakeS3()
        {
            Client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var request = ci.Arg<GetObjectRequest>();
                    Requests.Add(request);
                    var bytes = request.ByteRange is { } r
                        ? Object[(int)r.Start..((int)r.End + 1)]
                        : Object;
                    return Task.FromResult(Serve(bytes));
                });
            Client.GetObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(Serve(Object)));
        }

        private GetObjectResponse Serve(byte[] bytes)
        {
            BodyBytesServed += bytes.Length;
            return new GetObjectResponse { ResponseStream = new MemoryStream(bytes) };
        }
    }

    [Fact]
    public async Task OpenReadRangeAsync_asks_S3_for_only_the_range()
    {
        var s3 = new FakeS3();
        IStorageProvider provider = new S3StorageProvider(_ => s3.Client);

        await using var stream = await provider.OpenReadRangeAsync(Config(), "files/big.bin", offset: 1000, count: 10);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        Assert.Equal(s3.Object[1000..1010], copy.ToArray());
        // The bug's signature: the whole 4096-byte object was downloaded to serve 10 bytes.
        Assert.Equal(10, s3.BodyBytesServed);
        var request = Assert.Single(s3.Requests);
        Assert.NotNull(request.ByteRange);
        Assert.Equal(1000, request.ByteRange.Start);
        Assert.Equal(1009, request.ByteRange.End);
    }

    [Fact]
    public async Task OpenReadRangeAsync_of_zero_bytes_sends_no_request()
    {
        var s3 = new FakeS3();
        IStorageProvider provider = new S3StorageProvider(_ => s3.Client);

        await using var stream = await provider.OpenReadRangeAsync(Config(), "files/big.bin", offset: 5, count: 0);

        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
        Assert.Equal(0, s3.BodyBytesServed);
    }

    [Fact]
    public async Task OpenReadAsync_streams_the_response_and_disposing_it_releases_the_client()
    {
        var s3 = new FakeS3();
        IStorageProvider provider = new S3StorageProvider(_ => s3.Client);

        var stream = await provider.OpenReadAsync(Config(), "files/big.bin");
        // The default buffers the whole object into a MemoryStream.
        Assert.IsNotType<MemoryStream>(stream);
        s3.Client.DidNotReceive().Dispose();
        await stream.DisposeAsync();

        s3.Client.Received(1).Dispose();
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(@"nested\..\..\outside.txt")]
    public async Task OpenReadRangeAsync_WithTraversalKey_Throws(string fileKey)
    {
        IStorageProvider provider = new S3StorageProvider(_ => new FakeS3().Client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenReadRangeAsync(Config(), fileKey, 0, 1));
    }
}
