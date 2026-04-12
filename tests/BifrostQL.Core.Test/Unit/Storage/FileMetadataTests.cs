using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Test.Storage;

public class FileMetadataTests
{
    #region ToJson / FromJson

    [Fact]
    public void ToJson_SerializesAllProperties()
    {
        var metadata = new FileMetadata
        {
            FileKey = "test/file.txt",
            OriginalName = "original.txt",
            ContentType = "text/plain",
            Size = 1024,
            BucketName = "my-bucket",
            ProviderType = "local",
            UploadedAt = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            AccessUrl = "file:///path/to/file.txt"
        };

        var json = metadata.ToJson();

        Assert.Contains("\"FileKey\":\"test/file.txt\"", json);
        Assert.Contains("\"OriginalName\":\"original.txt\"", json);
        Assert.Contains("\"ContentType\":\"text/plain\"", json);
        Assert.Contains("\"Size\":1024", json);
        Assert.Contains("\"BucketName\":\"my-bucket\"", json);
        Assert.Contains("\"ProviderType\":\"local\"", json);
        Assert.Contains("\"AccessUrl\":\"file:///path/to/file.txt\"", json);
    }

    [Fact]
    public void FromJson_DeserializesAllProperties()
    {
        var json = @"{
            ""FileKey"": ""test/file.txt"",
            ""OriginalName"": ""original.txt"",
            ""ContentType"": ""text/plain"",
            ""Size"": 1024,
            ""BucketName"": ""my-bucket"",
            ""ProviderType"": ""local"",
            ""UploadedAt"": ""2024-01-15T10:30:00Z"",
            ""AccessUrl"": ""file:///path/to/file.txt""
        }";

        var metadata = FileMetadata.FromJson(json);

        Assert.NotNull(metadata);
        Assert.Equal("test/file.txt", metadata.FileKey);
        Assert.Equal("original.txt", metadata.OriginalName);
        Assert.Equal("text/plain", metadata.ContentType);
        Assert.Equal(1024, metadata.Size);
        Assert.Equal("my-bucket", metadata.BucketName);
        Assert.Equal("local", metadata.ProviderType);
        Assert.Equal("file:///path/to/file.txt", metadata.AccessUrl);
    }

    [Fact]
    public void FromJson_WithNull_ReturnsNull()
    {
        var metadata = FileMetadata.FromJson(null);

        Assert.Null(metadata);
    }

    [Fact]
    public void FromJson_WithEmptyString_ReturnsNull()
    {
        var metadata = FileMetadata.FromJson("");

        Assert.Null(metadata);
    }

    [Fact]
    public void FromJson_WithInvalidJson_ReturnsNull()
    {
        var metadata = FileMetadata.FromJson("not valid json");

        Assert.Null(metadata);
    }

    [Fact]
    public void FromJson_WithWhitespace_ReturnsNull()
    {
        var metadata = FileMetadata.FromJson("   ");

        Assert.Null(metadata);
    }

    #endregion

    #region GenerateFileKey

    [Fact]
    public void GenerateFileKey_IncludesTableColumnAndRecordId()
    {
        var key = FileMetadata.GenerateFileKey("Users", "Avatar", "123");

        Assert.Contains("Users", key);
        Assert.Contains("Avatar", key);
        Assert.Contains("123", key);
    }

    [Fact]
    public void GenerateFileKey_WithOriginalFileName_IncludesExtension()
    {
        var key = FileMetadata.GenerateFileKey("Users", "Avatar", "123", "photo.png");

        Assert.EndsWith(".png", key);
    }

    [Fact]
    public void GenerateFileKey_WithInvalidCharacters_SanitizesPath()
    {
        var key = FileMetadata.GenerateFileKey("User<Table>", "Col:Name", "123:456");

        Assert.DoesNotContain("<", key);
        Assert.DoesNotContain(">", key);
        Assert.DoesNotContain(":", key);
        // Note: / is used as path separator and is valid in the result
    }

    [Fact]
    public void GenerateFileKey_GeneratesUniqueKeys()
    {
        var key1 = FileMetadata.GenerateFileKey("Users", "Avatar", "123");
        var key2 = FileMetadata.GenerateFileKey("Users", "Avatar", "123");

        Assert.NotEqual(key1, key2);
    }

    #endregion
}
