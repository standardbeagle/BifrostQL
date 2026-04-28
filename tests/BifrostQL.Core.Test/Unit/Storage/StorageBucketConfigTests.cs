using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Test.Storage;

public class StorageBucketConfigTests
{
    #region FromMetadata - Basic Parsing

    [Fact]
    public void FromMetadata_WithValidConfig_ParsesBucketName()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket");

        Assert.NotNull(config);
        Assert.Equal("my-bucket", config.BucketName);
    }

    [Fact]
    public void FromMetadata_WithProvider_ParsesProviderType()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket;provider:s3");

        Assert.NotNull(config);
        Assert.Equal("my-bucket", config.BucketName);
        Assert.Equal("s3", config.ProviderType);
    }

    [Fact]
    public void FromMetadata_WithPrefix_ParsesPathPrefix()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket;prefix:uploads/2024");

        Assert.NotNull(config);
        Assert.Equal("uploads/2024", config.PathPrefix);
    }

    [Fact]
    public void FromMetadata_WithRegion_ParsesRegion()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket;region:us-east-1");

        Assert.NotNull(config);
        Assert.Equal("us-east-1", config.Region);
    }

    [Fact]
    public void FromMetadata_WithEndpoint_ParsesEndpointUrl()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket;endpoint:https://s3.example.com");

        Assert.NotNull(config);
        Assert.Equal("https://s3.example.com", config.EndpointUrl);
    }

    [Fact]
    public void FromMetadata_WithMaxSize_ParsesMaxFileSize()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket;maxSize:5242880");

        Assert.NotNull(config);
        Assert.Equal(5242880, config.MaxFileSize);
    }

    [Fact]
    public void FromMetadata_WithMimeTypes_ParsesAllowedMimeTypes()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket;mimetypes:image/*,application/pdf");

        Assert.NotNull(config);
        Assert.NotNull(config.AllowedMimeTypes);
        Assert.Equal(2, config.AllowedMimeTypes.Length);
        Assert.Contains("image/*", config.AllowedMimeTypes);
        Assert.Contains("application/pdf", config.AllowedMimeTypes);
    }

    #endregion

    #region FromMetadata - Default Values

    [Fact]
    public void FromMetadata_WithoutProvider_DefaultsToLocal()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket");

        Assert.NotNull(config);
        Assert.Equal("local", config.ProviderType);
    }

    [Fact]
    public void FromMetadata_WithoutMaxSize_DefaultsTo10MB()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket");

        Assert.NotNull(config);
        Assert.Equal(10 * 1024 * 1024, config.MaxFileSize);
    }

    [Fact]
    public void FromMetadata_WithoutPathPrefix_IsNull()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket");

        Assert.NotNull(config);
        Assert.Null(config.PathPrefix);
    }

    #endregion

    #region FromMetadata - Edge Cases

    [Fact]
    public void FromMetadata_WithNull_ReturnsNull()
    {
        var config = StorageBucketConfig.FromMetadata(null);

        Assert.Null(config);
    }

    [Fact]
    public void FromMetadata_WithEmptyString_ReturnsNull()
    {
        var config = StorageBucketConfig.FromMetadata("");

        Assert.Null(config);
    }

    [Fact]
    public void FromMetadata_WithWhitespace_ReturnsNull()
    {
        var config = StorageBucketConfig.FromMetadata("   ");

        Assert.Null(config);
    }

    [Fact]
    public void FromMetadata_WithoutBucketName_ReturnsNull()
    {
        var config = StorageBucketConfig.FromMetadata("provider:s3;region:us-east-1");

        Assert.Null(config);
    }

    [Fact]
    public void FromMetadata_WithInvalidMaxSize_IgnoresValue()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket;maxSize:invalid");

        Assert.NotNull(config);
        Assert.Equal(10 * 1024 * 1024, config.MaxFileSize);
    }

    #endregion

    #region FromMetadata - Alternative Property Names

    [Fact]
    public void FromMetadata_WithBucketNameAlias_ParsesCorrectly()
    {
        var config = StorageBucketConfig.FromMetadata("bucketname:my-bucket");

        Assert.NotNull(config);
        Assert.Equal("my-bucket", config.BucketName);
    }

    [Fact]
    public void FromMetadata_WithProviderTypeAlias_ParsesCorrectly()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket;providertype:azure");

        Assert.NotNull(config);
        Assert.Equal("azure", config.ProviderType);
    }

    [Fact]
    public void FromMetadata_WithPathPrefixAlias_ParsesCorrectly()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket;pathprefix:files");

        Assert.NotNull(config);
        Assert.Equal("files", config.PathPrefix);
    }

    [Fact]
    public void FromMetadata_WithMaxFileSizeAlias_ParsesCorrectly()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket;maxfilesize:2097152");

        Assert.NotNull(config);
        Assert.Equal(2097152, config.MaxFileSize);
    }

    [Fact]
    public void FromMetadata_WithAllowedMimeTypesAlias_ParsesCorrectly()
    {
        var config = StorageBucketConfig.FromMetadata("bucket:my-bucket;allowedmimetypes:image/png");

        Assert.NotNull(config);
        Assert.NotNull(config.AllowedMimeTypes);
        Assert.Contains("image/png", config.AllowedMimeTypes);
    }

    #endregion

    #region GetFullPath

    [Fact]
    public void GetFullPath_WithPrefix_ReturnsPrefixedPath()
    {
        var config = new StorageBucketConfig
        {
            BucketName = "my-bucket",
            PathPrefix = "uploads/2024"
        };

        var result = config.GetFullPath("file.txt");

        Assert.Equal("uploads/2024/file.txt", result);
    }

    [Fact]
    public void GetFullPath_WithoutPrefix_ReturnsFileKey()
    {
        var config = new StorageBucketConfig
        {
            BucketName = "my-bucket"
        };

        var result = config.GetFullPath("file.txt");

        Assert.Equal("file.txt", result);
    }

    [Fact]
    public void GetFullPath_WithTrailingSlashInPrefix_HandlesCorrectly()
    {
        var config = new StorageBucketConfig
        {
            BucketName = "my-bucket",
            PathPrefix = "uploads/2024/"
        };

        var result = config.GetFullPath("file.txt");

        Assert.Equal("uploads/2024/file.txt", result);
    }

    #endregion
}
