using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Test.Storage;

public class FileColumnConfigTests
{
    #region FromMetadata - Basic Parsing

    [Fact]
    public void FromMetadata_WithType_ParsesFileType()
    {
        var config = FileColumnConfig.FromMetadata("type:image");

        Assert.NotNull(config);
        Assert.Equal("image", config.FileType);
    }

    [Fact]
    public void FromMetadata_WithMaxSize_ParsesMaxFileSize()
    {
        var config = FileColumnConfig.FromMetadata("type:image;maxSize:5242880");

        Assert.NotNull(config);
        Assert.Equal(5242880, config.MaxFileSize);
    }

    [Fact]
    public void FromMetadata_WithAccept_ParsesMimeTypes()
    {
        var config = FileColumnConfig.FromMetadata("accept:image/*,image/png,image/jpeg");

        Assert.NotNull(config);
        Assert.NotNull(config.AcceptMimeTypes);
        Assert.Equal(3, config.AcceptMimeTypes.Length);
    }

    [Fact]
    public void FromMetadata_WithThumbnails_ParsesGenerateThumbnails()
    {
        var config = FileColumnConfig.FromMetadata("thumbnails:true");

        Assert.NotNull(config);
        Assert.True(config.GenerateThumbnails);
    }

    [Fact]
    public void FromMetadata_WithSizes_ParsesThumbnailSizes()
    {
        var config = FileColumnConfig.FromMetadata("sizes:100x100,300x300,800x600");

        Assert.NotNull(config);
        Assert.NotNull(config.ThumbnailSizes);
        Assert.Equal(3, config.ThumbnailSizes.Length);
        Assert.Contains("100x100", config.ThumbnailSizes);
    }

    [Fact]
    public void FromMetadata_WithPublic_ParsesPublicAccess()
    {
        var config = FileColumnConfig.FromMetadata("public:true");

        Assert.NotNull(config);
        Assert.True(config.PublicAccess);
    }

    [Fact]
    public void FromMetadata_WithPath_ParsesCustomPath()
    {
        var config = FileColumnConfig.FromMetadata("path:uploads/images");

        Assert.NotNull(config);
        Assert.Equal("uploads/images", config.CustomPath);
    }

    #endregion

    #region FromMetadata - Edge Cases

    [Fact]
    public void FromMetadata_WithNull_ReturnsNull()
    {
        var config = FileColumnConfig.FromMetadata(null);

        Assert.Null(config);
    }

    [Fact]
    public void FromMetadata_WithEmptyString_ReturnsNull()
    {
        var config = FileColumnConfig.FromMetadata("");

        Assert.Null(config);
    }

    [Fact]
    public void FromMetadata_WithInvalidMaxSize_IgnoresValue()
    {
        var config = FileColumnConfig.FromMetadata("maxSize:invalid");

        Assert.NotNull(config);
        Assert.Null(config.MaxFileSize);
    }

    #endregion

    #region GetAcceptAttribute

    [Fact]
    public void GetAcceptAttribute_WithExplicitMimeTypes_ReturnsJoinedString()
    {
        var config = new FileColumnConfig
        {
            AcceptMimeTypes = new[] { "image/png", "image/jpeg", "image/gif" }
        };

        var result = config.GetAcceptAttribute();

        Assert.Equal("image/png,image/jpeg,image/gif", result);
    }

    [Theory]
    [InlineData("image", "image/*")]
    [InlineData("img", "image/*")]
    [InlineData("picture", "image/*")]
    [InlineData("document", ".pdf,.doc,.docx,.txt,.rtf")]
    [InlineData("doc", ".pdf,.doc,.docx,.txt,.rtf")]
    [InlineData("video", "video/*")]
    [InlineData("audio", "audio/*")]
    [InlineData("archive", ".zip,.rar,.7z,.tar,.gz")]
    [InlineData("zip", ".zip,.rar,.7z,.tar,.gz")]
    public void GetAcceptAttribute_WithFileType_ReturnsExpectedValue(string fileType, string expected)
    {
        var config = new FileColumnConfig
        {
            FileType = fileType
        };

        var result = config.GetAcceptAttribute();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetAcceptAttribute_WithUnknownFileType_ReturnsNull()
    {
        var config = new FileColumnConfig
        {
            FileType = "unknown"
        };

        var result = config.GetAcceptAttribute();

        Assert.Null(result);
    }

    [Fact]
    public void GetAcceptAttribute_WithNoConfig_ReturnsNull()
    {
        var config = new FileColumnConfig();

        var result = config.GetAcceptAttribute();

        Assert.Null(result);
    }

    [Fact]
    public void GetAcceptAttribute_MimeTypesTakePrecedenceOverFileType()
    {
        var config = new FileColumnConfig
        {
            FileType = "image",
            AcceptMimeTypes = new[] { "image/png" }
        };

        var result = config.GetAcceptAttribute();

        Assert.Equal("image/png", result);
    }

    #endregion
}
