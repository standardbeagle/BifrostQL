using BifrostQL.Core.Forms;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Test.Forms;

public class FileUploadHandlerTests
{
    #region GenerateFileInput - Basic Structure

    [Fact]
    public void GenerateFileInput_GeneratesFileInputElement()
    {
        var column = CreateColumn("ProfileImage", "varbinary");

        var html = FileUploadHandler.GenerateFileInput(column);

        Assert.Contains("<input type=\"file\"", html);
        Assert.Contains("name=\"ProfileImage\"", html);
        Assert.Contains("id=\"profileimage\"", html);
    }

    [Fact]
    public void GenerateFileInput_IncludesDefaultAcceptAttribute()
    {
        var column = CreateColumn("Photo", "image");

        var html = FileUploadHandler.GenerateFileInput(column);

        Assert.Contains("accept=\"image/*\"", html);
    }

    #endregion

    #region GenerateFileInput - Custom Accept

    [Fact]
    public void GenerateFileInput_WithCustomAccept_UsesMetadataAccept()
    {
        var column = CreateColumn("Document", "varbinary");
        var metadata = new ColumnMetadata { Accept = "application/pdf,.doc,.docx" };

        var html = FileUploadHandler.GenerateFileInput(column, metadata);

        Assert.Contains("accept=\"application/pdf,.doc,.docx\"", html);
        Assert.DoesNotContain("image/*", html);
    }

    [Fact]
    public void GenerateFileInput_WithNullMetadata_UsesDefaultAccept()
    {
        var column = CreateColumn("Photo", "varbinary");

        var html = FileUploadHandler.GenerateFileInput(column, metadata: null);

        Assert.Contains("accept=\"image/*\"", html);
    }

    [Fact]
    public void GenerateFileInput_WithMetadataNoAccept_UsesDefaultAccept()
    {
        var column = CreateColumn("Photo", "varbinary");
        var metadata = new ColumnMetadata { InputType = "file" };

        var html = FileUploadHandler.GenerateFileInput(column, metadata);

        Assert.Contains("accept=\"image/*\"", html);
    }

    #endregion

    #region GenerateFileInput - Current Value Help Text

    [Fact]
    public void GenerateFileInput_WithNoCurrentValue_NoHelpText()
    {
        var column = CreateColumn("Photo", "varbinary");

        var html = FileUploadHandler.GenerateFileInput(column, hasCurrentValue: false);

        Assert.DoesNotContain("help-text", html);
        Assert.DoesNotContain("Leave empty", html);
    }

    [Fact]
    public void GenerateFileInput_WithCurrentValue_ShowsHelpText()
    {
        var column = CreateColumn("Photo", "varbinary");

        var html = FileUploadHandler.GenerateFileInput(column, hasCurrentValue: true);

        Assert.Contains("class=\"help-text\"", html);
        Assert.Contains("Leave empty to keep current file", html);
    }

    #endregion

    #region GenerateFileInput - XSS Protection

    [Fact]
    public void GenerateFileInput_HtmlEncodesColumnName()
    {
        var column = CreateColumn("file<name>", "varbinary");

        var html = FileUploadHandler.GenerateFileInput(column);

        Assert.DoesNotContain("<name>", html);
        Assert.Contains("file&lt;name&gt;", html);
    }

    [Fact]
    public void GenerateFileInput_HtmlEncodesAcceptAttribute()
    {
        var column = CreateColumn("Doc", "varbinary");
        var metadata = new ColumnMetadata { Accept = "text/plain\"><script>" };

        var html = FileUploadHandler.GenerateFileInput(column, metadata);

        Assert.DoesNotContain("<script>", html);
    }

    #endregion

    #region Helper Methods

    private static ColumnDto CreateColumn(string name, string dataType, bool isNullable = true)
    {
        return new ColumnDto
        {
            ColumnName = name,
            GraphQlName = name.ToLowerInvariant(),
            NormalizedName = name,
            DataType = dataType,
            IsNullable = isNullable
        };
    }

    #endregion
}
