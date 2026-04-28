using BifrostQL.Core.Forms;

namespace BifrostQL.Core.Test.Forms;

public class FormsMetadataConfigurationTests
{
    #region ConfigureColumn

    [Fact]
    public void ConfigureColumn_SetsMetadata()
    {
        var config = new FormsMetadataConfiguration();

        config.ConfigureColumn("Users", "Email", col =>
        {
            col.InputType = "email";
            col.Placeholder = "user@example.com";
        });

        var metadata = config.GetMetadata("Users", "Email");
        Assert.NotNull(metadata);
        Assert.Equal("email", metadata!.InputType);
        Assert.Equal("user@example.com", metadata.Placeholder);
    }

    [Fact]
    public void ConfigureColumn_IsCaseInsensitive()
    {
        var config = new FormsMetadataConfiguration();
        config.ConfigureColumn("Users", "Email", col => col.InputType = "email");

        var metadata = config.GetMetadata("users", "email");

        Assert.NotNull(metadata);
        Assert.Equal("email", metadata!.InputType);
    }

    [Fact]
    public void ConfigureColumn_MultipleCallsSameColumn_MergesMetadata()
    {
        var config = new FormsMetadataConfiguration();
        config.ConfigureColumn("Users", "Age", col => col.Min = 0);
        config.ConfigureColumn("Users", "Age", col => col.Max = 120);

        var metadata = config.GetMetadata("Users", "Age");
        Assert.NotNull(metadata);
        Assert.Equal(0, metadata!.Min);
        Assert.Equal(120, metadata.Max);
    }

    [Fact]
    public void ConfigureColumn_ReturnsSelfForFluency()
    {
        var config = new FormsMetadataConfiguration();

        var result = config
            .ConfigureColumn("Users", "Email", col => col.InputType = "email")
            .ConfigureColumn("Users", "Age", col => col.Min = 0);

        Assert.Same(config, result);
    }

    [Fact]
    public void ConfigureColumn_DifferentColumns_StoredSeparately()
    {
        var config = new FormsMetadataConfiguration();
        config.ConfigureColumn("Users", "Email", col => col.InputType = "email");
        config.ConfigureColumn("Users", "Phone", col => col.InputType = "tel");

        var emailMeta = config.GetMetadata("Users", "Email");
        var phoneMeta = config.GetMetadata("Users", "Phone");

        Assert.Equal("email", emailMeta!.InputType);
        Assert.Equal("tel", phoneMeta!.InputType);
    }

    [Fact]
    public void ConfigureColumn_DifferentTables_StoredSeparately()
    {
        var config = new FormsMetadataConfiguration();
        config.ConfigureColumn("Users", "Name", col => col.Placeholder = "User name");
        config.ConfigureColumn("Products", "Name", col => col.Placeholder = "Product name");

        var userMeta = config.GetMetadata("Users", "Name");
        var productMeta = config.GetMetadata("Products", "Name");

        Assert.Equal("User name", userMeta!.Placeholder);
        Assert.Equal("Product name", productMeta!.Placeholder);
    }

    #endregion

    #region GetMetadata

    [Fact]
    public void GetMetadata_UnconfiguredColumn_ReturnsNull()
    {
        var config = new FormsMetadataConfiguration();

        var metadata = config.GetMetadata("Users", "Name");

        Assert.Null(metadata);
    }

    [Fact]
    public void GetMetadata_UnconfiguredTable_ReturnsNull()
    {
        var config = new FormsMetadataConfiguration();
        config.ConfigureColumn("Users", "Email", col => col.InputType = "email");

        var metadata = config.GetMetadata("Products", "Email");

        Assert.Null(metadata);
    }

    #endregion

    #region Enum Configuration

    [Fact]
    public void ConfigureColumn_EnumValues_StoresEnumConfig()
    {
        var config = new FormsMetadataConfiguration();
        config.ConfigureColumn("Users", "Status", col =>
        {
            col.EnumValues = new[] { "active", "inactive", "pending" };
            col.EnumDisplayNames = new Dictionary<string, string>
            {
                ["active"] = "Active",
                ["inactive"] = "Inactive",
                ["pending"] = "Pending Approval"
            };
        });

        var metadata = config.GetMetadata("Users", "Status");
        Assert.NotNull(metadata);
        Assert.Equal(3, metadata!.EnumValues!.Length);
        Assert.Equal("Active", metadata.EnumDisplayNames!["active"]);
        Assert.Equal("Pending Approval", metadata.EnumDisplayNames["pending"]);
    }

    #endregion

    #region All Properties

    [Fact]
    public void ConfigureColumn_AllProperties_StoresAll()
    {
        var config = new FormsMetadataConfiguration();
        config.ConfigureColumn("Products", "Price", col =>
        {
            col.InputType = "number";
            col.Placeholder = "0.00";
            col.Pattern = "[0-9]+";
            col.Min = 0;
            col.Max = 999999.99;
            col.Step = 0.01;
            col.Accept = "image/png";
            col.EnumValues = new[] { "a" };
            col.EnumDisplayNames = new Dictionary<string, string> { ["a"] = "A" };
            col.MinLength = 1;
            col.MaxLength = 100;
            col.Required = true;
            col.Title = "Hint text";
        });

        var metadata = config.GetMetadata("Products", "Price");
        Assert.NotNull(metadata);
        Assert.Equal("number", metadata!.InputType);
        Assert.Equal("0.00", metadata.Placeholder);
        Assert.Equal("[0-9]+", metadata.Pattern);
        Assert.Equal(0, metadata.Min);
        Assert.Equal(999999.99, metadata.Max);
        Assert.Equal(0.01, metadata.Step);
        Assert.Equal("image/png", metadata.Accept);
        Assert.Single(metadata.EnumValues!);
        Assert.Single(metadata.EnumDisplayNames!);
        Assert.Equal(1, metadata.MinLength);
        Assert.Equal(100, metadata.MaxLength);
        Assert.True(metadata.Required);
        Assert.Equal("Hint text", metadata.Title);
    }

    #endregion
}
