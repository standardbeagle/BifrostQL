using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Test.Forms;

public class FileUploadHandlerStorageTests
{
    private static ColumnDto CreateColumn(string name, string dataType, Dictionary<string, object?>? metadata = null)
    {
        return new ColumnDto
        {
            ColumnName = name,
            GraphQlName = name.ToLowerInvariant(),
            NormalizedName = name,
            DataType = dataType,
            Metadata = metadata ?? new Dictionary<string, object?>()
        };
    }

    #region IsFileColumn

    [Fact]
    public void IsFileColumn_WithFileMetadata_ReturnsTrue()
    {
        var column = CreateColumn("avatar", "nvarchar", new Dictionary<string, object?>
        {
            ["file"] = "type:image"
        });

        var result = FileUploadHandler.IsFileColumn(column);

        Assert.True(result);
    }

    [Fact]
    public void IsFileColumn_WithStorageMetadata_ReturnsTrue()
    {
        var column = CreateColumn("document", "nvarchar", new Dictionary<string, object?>
        {
            ["storage"] = "bucket:files"
        });

        var result = FileUploadHandler.IsFileColumn(column);

        Assert.True(result);
    }

    [Fact]
    public void IsFileColumn_WithBinaryType_ReturnsTrue()
    {
        var column = CreateColumn("data", "varbinary");

        var result = FileUploadHandler.IsFileColumn(column);

        Assert.True(result);
    }

    [Fact]
    public void IsFileColumn_WithMetadataInputTypeFile_ReturnsTrue()
    {
        var column = CreateColumn("attachment", "nvarchar");
        var metadata = new ColumnMetadata { InputType = "file" };

        var result = FileUploadHandler.IsFileColumn(column, metadata);

        Assert.True(result);
    }

    [Fact]
    public void IsFileColumn_WithRegularColumn_ReturnsFalse()
    {
        var column = CreateColumn("name", "nvarchar");

        var result = FileUploadHandler.IsFileColumn(column);

        Assert.False(result);
    }

    #endregion

    #region GetAcceptAttribute

    [Fact]
    public void GetAcceptAttribute_WithMetadataAccept_ReturnsMetadataValue()
    {
        var column = CreateColumn("avatar", "nvarchar");
        var metadata = new ColumnMetadata { Accept = "image/png,image/jpeg" };

        var result = FileUploadHandler.GetAcceptAttribute(column, metadata);

        Assert.Equal("image/png,image/jpeg", result);
    }

    [Fact]
    public void GetAcceptAttribute_WithFileConfig_ReturnsConfigValue()
    {
        var column = CreateColumn("avatar", "nvarchar", new Dictionary<string, object?>
        {
            ["file"] = "type:image;accept:image/*"
        });

        var result = FileUploadHandler.GetAcceptAttribute(column);

        Assert.Equal("image/*", result);
    }

    [Fact]
    public void GetAcceptAttribute_WithBinaryType_ReturnsDefaultImageAccept()
    {
        var column = CreateColumn("data", "varbinary");

        var result = FileUploadHandler.GetAcceptAttribute(column);

        Assert.Equal("image/*", result);
    }

    [Fact]
    public void GetAcceptAttribute_WithUnknownType_ReturnsWildcard()
    {
        var column = CreateColumn("name", "nvarchar");

        var result = FileUploadHandler.GetAcceptAttribute(column);

        Assert.Equal("*/*", result);
    }

    #endregion

    #region GenerateFileInput

    [Fact]
    public void GenerateFileInput_WithFileMetadata_AddsDataAttribute()
    {
        var column = CreateColumn("avatar", "nvarchar", new Dictionary<string, object?>
        {
            ["file"] = "type:image"
        });

        var html = FileUploadHandler.GenerateFileInput(column);

        Assert.Contains("data-file-storage=\"type:image\"", html);
    }

    [Fact]
    public void GenerateFileInput_WithStorageMetadata_AddsDataAttribute()
    {
        var column = CreateColumn("document", "nvarchar", new Dictionary<string, object?>
        {
            ["storage"] = "bucket:files"
        });

        var html = FileUploadHandler.GenerateFileInput(column);

        Assert.Contains("data-storage=\"bucket:files\"", html);
    }

    [Fact]
    public void GenerateFileInput_WithFileTypeConfig_UsesCorrectAccept()
    {
        var column = CreateColumn("document", "nvarchar", new Dictionary<string, object?>
        {
            ["file"] = "type:document"
        });

        var html = FileUploadHandler.GenerateFileInput(column);

        Assert.Contains("accept=\".pdf,.doc,.docx,.txt,.rtf\"", html);
    }

    [Fact]
    public void GenerateFileInput_HtmlEncodesDataAttribute()
    {
        var column = CreateColumn("avatar", "nvarchar", new Dictionary<string, object?>
        {
            ["file"] = "type:image\" onclick=\"alert(1)"
        });

        var html = FileUploadHandler.GenerateFileInput(column);

        Assert.DoesNotContain("onclick=\"alert(1)\"", html);
        Assert.Contains("&quot;", html);
    }

    #endregion
}
