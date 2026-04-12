using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Test.Storage;

public class StorageProviderFactoryTests
{
    #region Constructor

    [Fact]
    public void Constructor_RegistersLocalProvider()
    {
        var factory = new StorageProviderFactory();

        Assert.True(factory.IsSupported("local"));
    }

    #endregion

    #region GetProvider

    [Fact]
    public void GetProvider_WithLocal_ReturnsLocalStorageProvider()
    {
        var factory = new StorageProviderFactory();

        var provider = factory.GetProvider("local");

        Assert.IsType<LocalStorageProvider>(provider);
    }

    [Fact]
    public void GetProvider_WithUnsupportedType_ThrowsNotSupportedException()
    {
        var factory = new StorageProviderFactory();

        Assert.Throws<NotSupportedException>(() => factory.GetProvider("unsupported"));
    }

    [Fact]
    public void GetProvider_IsCaseInsensitive()
    {
        var factory = new StorageProviderFactory();

        var provider1 = factory.GetProvider("LOCAL");
        var provider2 = factory.GetProvider("Local");

        Assert.NotNull(provider1);
        Assert.NotNull(provider2);
    }

    [Fact]
    public void GetProvider_WithConfig_ReturnsCorrectProvider()
    {
        var factory = new StorageProviderFactory();
        var config = new StorageBucketConfig 
        { 
            ProviderType = "local",
            BucketName = "test"
        };

        var provider = factory.GetProvider(config);

        Assert.IsType<LocalStorageProvider>(provider);
    }

    #endregion

    #region RegisterProvider

    [Fact]
    public void RegisterProvider_AddsCustomProvider()
    {
        var factory = new StorageProviderFactory();
        var customProvider = new TestStorageProvider();

        factory.RegisterProvider(customProvider);

        Assert.True(factory.IsSupported("test"));
        var retrieved = factory.GetProvider("test");
        Assert.Same(customProvider, retrieved);
    }

    [Fact]
    public void RegisterProvider_WithSameType_ReplacesExisting()
    {
        var factory = new StorageProviderFactory();
        var customProvider = new TestStorageProvider();
        factory.RegisterProvider(customProvider);
        var replacement = new TestStorageProvider();

        factory.RegisterProvider(replacement);

        var retrieved = factory.GetProvider("test");
        Assert.Same(replacement, retrieved);
    }

    #endregion

    #region IsSupported

    [Fact]
    public void IsSupported_WithRegisteredProvider_ReturnsTrue()
    {
        var factory = new StorageProviderFactory();

        Assert.True(factory.IsSupported("local"));
    }

    [Fact]
    public void IsSupported_WithUnregisteredProvider_ReturnsFalse()
    {
        var factory = new StorageProviderFactory();

        Assert.False(factory.IsSupported("s3"));
    }

    #endregion

    private class TestStorageProvider : IStorageProvider
    {
        public string ProviderType => "test";

        public Task<string> UploadAsync(StorageBucketConfig bucketConfig, string fileKey, byte[] content, string? contentType = null, CancellationToken cancellationToken = default)
            => Task.FromResult("");

        public Task<byte[]> DownloadAsync(StorageBucketConfig bucketConfig, string fileKey, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<byte>());

        public Task DeleteAsync(StorageBucketConfig bucketConfig, string fileKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> ExistsAsync(StorageBucketConfig bucketConfig, string fileKey, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<string> GetPresignedUrlAsync(StorageBucketConfig bucketConfig, string fileKey, int expirationMinutes = 15, bool forUpload = false)
            => Task.FromResult("");
    }
}
