using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Test.Storage;

public class LocalStorageProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalStorageProvider _provider;

    public LocalStorageProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _provider = new LocalStorageProvider();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    #region ProviderType

    [Fact]
    public void ProviderType_ReturnsLocal()
    {
        Assert.Equal("local", _provider.ProviderType);
    }

    #endregion

    #region UploadAsync

    [Fact]
    public async Task UploadAsync_CreatesFile()
    {
        var config = new StorageBucketConfig { BucketName = _tempDir };
        var content = new byte[] { 1, 2, 3, 4, 5 };

        var path = await _provider.UploadAsync(config, "test.txt", content);

        Assert.True(File.Exists(path));
        var savedContent = await File.ReadAllBytesAsync(path);
        Assert.Equal(content, savedContent);
    }

    [Fact]
    public async Task UploadAsync_CreatesDirectories()
    {
        var config = new StorageBucketConfig { BucketName = _tempDir };
        var content = new byte[] { 1, 2, 3 };

        var path = await _provider.UploadAsync(config, "subdir/nested/file.txt", content);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task UploadAsync_WithPrefix_IncludesPrefixInPath()
    {
        var config = new StorageBucketConfig 
        { 
            BucketName = _tempDir,
            PathPrefix = "uploads"
        };
        var content = new byte[] { 1, 2, 3 };

        var path = await _provider.UploadAsync(config, "file.txt", content);

        Assert.Contains("uploads", path);
        Assert.True(File.Exists(path));
    }

    #endregion

    #region DownloadAsync

    [Fact]
    public async Task DownloadAsync_ReturnsFileContent()
    {
        var config = new StorageBucketConfig { BucketName = _tempDir };
        var content = new byte[] { 1, 2, 3, 4, 5 };
        var path = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllBytesAsync(path, content);

        var downloaded = await _provider.DownloadAsync(config, "test.txt");

        Assert.Equal(content, downloaded);
    }

    [Fact]
    public async Task DownloadAsync_WithNonExistentFile_ThrowsFileNotFoundException()
    {
        var config = new StorageBucketConfig { BucketName = _tempDir };

        await Assert.ThrowsAsync<FileNotFoundException>(() => 
            _provider.DownloadAsync(config, "nonexistent.txt"));
    }

    #endregion

    #region DeleteAsync

    [Fact]
    public async Task DeleteAsync_RemovesFile()
    {
        var config = new StorageBucketConfig { BucketName = _tempDir };
        var path = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });

        await _provider.DeleteAsync(config, "test.txt");

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistentFile_DoesNotThrow()
    {
        var config = new StorageBucketConfig { BucketName = _tempDir };

        await _provider.DeleteAsync(config, "nonexistent.txt");

        // Should not throw
    }

    #endregion

    #region ExistsAsync

    [Fact]
    public async Task ExistsAsync_WithExistingFile_ReturnsTrue()
    {
        var config = new StorageBucketConfig { BucketName = _tempDir };
        var path = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });

        var exists = await _provider.ExistsAsync(config, "test.txt");

        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsAsync_WithNonExistentFile_ReturnsFalse()
    {
        var config = new StorageBucketConfig { BucketName = _tempDir };

        var exists = await _provider.ExistsAsync(config, "nonexistent.txt");

        Assert.False(exists);
    }

    #endregion

    #region GetPresignedUrlAsync

    [Fact]
    public async Task GetPresignedUrlAsync_ReturnsFilePath()
    {
        var config = new StorageBucketConfig { BucketName = _tempDir };

        var url = await _provider.GetPresignedUrlAsync(config, "test.txt");

        Assert.StartsWith("file://", url);
        Assert.Contains("test.txt", url);
    }

    #endregion
}
