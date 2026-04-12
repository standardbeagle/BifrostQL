using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model.AppSchema;

public class WordPressBundleExtensionsTests
{
    #region Helper Methods

    private static IDbTable MakeTable(string dbName, string schema = "dbo")
        => new DbTable
        {
            DbName = dbName,
            GraphQlName = dbName,
            NormalizedName = dbName,
            TableSchema = schema,
            TableType = "BASE TABLE",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
            SingleLinks = new Dictionary<string, TableLinkDto>(),
            MultiLinks = new Dictionary<string, TableLinkDto>(),
            ManyToManyLinks = new Dictionary<string, ManyToManyLink>(),
        };

    private static ColumnDto MakeColumn(string name, string dataType = "nvarchar")
        => new()
        {
            TableName = "test",
            TableSchema = "dbo",
            ColumnName = name,
            GraphQlName = name,
            NormalizedName = name,
            DataType = dataType,
            IsNullable = true,
            Metadata = new Dictionary<string, object?>()
        };

    #endregion

    #region IsWordPressMetaTable

    [Theory]
    [InlineData("wp_postmeta", true)]
    [InlineData("wp_usermeta", true)]
    [InlineData("wp_termmeta", true)]
    [InlineData("wp_commentmeta", true)]
    [InlineData("wp_posts", false)]
    [InlineData("wp_users", false)]
    [InlineData("blog_postmeta", true)]
    [InlineData("custom_usermeta", true)]
    [InlineData("orders", false)]
    public void IsWordPressMetaTable_VariousTables_ReturnsExpected(string tableName, bool expected)
    {
        var table = MakeTable(tableName);

        var result = table.IsWordPressMetaTable();

        result.Should().Be(expected);
    }

    [Fact]
    public void IsWordPressMetaTable_CaseInsensitive_ReturnsTrue()
    {
        var table = MakeTable("WP_POSTMETA");

        var result = table.IsWordPressMetaTable();

        result.Should().BeTrue();
    }

    #endregion

    #region IsWordPressCoreTable

    [Theory]
    [InlineData("wp_posts", true)]
    [InlineData("wp_users", true)]
    [InlineData("wp_options", true)]
    [InlineData("wp_comments", true)]
    [InlineData("wp_terms", true)]
    [InlineData("wp_links", true)]
    [InlineData("wp_postmeta", false)]
    [InlineData("blog_posts", true)]
    [InlineData("orders", false)]
    public void IsWordPressCoreTable_VariousTables_ReturnsExpected(string tableName, bool expected)
    {
        var table = MakeTable(tableName);

        var result = table.IsWordPressCoreTable();

        result.Should().Be(expected);
    }

    #endregion

    #region IsPhpSerialized

    [Fact]
    public void IsPhpSerialized_WithTypeMetadata_ReturnsTrue()
    {
        var column = MakeColumn("meta_value");
        column.Metadata["type"] = "php_serialized";

        var result = column.IsPhpSerialized();

        result.Should().BeTrue();
    }

    [Fact]
    public void IsPhpSerialized_WithoutMetadata_ReturnsFalse()
    {
        var column = MakeColumn("meta_value");

        var result = column.IsPhpSerialized();

        result.Should().BeFalse();
    }

    [Fact]
    public void IsPhpSerialized_WithDifferentType_ReturnsFalse()
    {
        var column = MakeColumn("meta_value");
        column.Metadata["type"] = "string";

        var result = column.IsPhpSerialized();

        result.Should().BeFalse();
    }

    [Fact]
    public void IsPhpSerialized_CaseInsensitive_ReturnsTrue()
    {
        var column = MakeColumn("meta_value");
        column.Metadata["type"] = "PHP_SERIALIZED";

        var result = column.IsPhpSerialized();

        result.Should().BeTrue();
    }

    #endregion

    #region GetWordPressTableType

    [Theory]
    [InlineData("wp_posts", "posts")]
    [InlineData("wp_users", "users")]
    [InlineData("wp_options", "options")]
    [InlineData("wp_postmeta", "postmeta")]
    [InlineData("blog_comments", "comments")]
    [InlineData("posts", "posts")]
    public void GetWordPressTableType_VariousTables_ReturnsExpected(string tableName, string expected)
    {
        var table = MakeTable(tableName);

        var result = table.GetWordPressTableType();

        result.Should().Be(expected);
    }

    [Fact]
    public void GetWordPressTableType_NoUnderscore_ReturnsFullName()
    {
        var table = MakeTable("posts");

        var result = table.GetWordPressTableType();

        result.Should().Be("posts");
    }

    #endregion

    #region IsActionSchedulerTable

    [Theory]
    [InlineData("wp_actionscheduler_actions", true)]
    [InlineData("wp_actionscheduler_claims", true)]
    [InlineData("wp_actionscheduler_groups", true)]
    [InlineData("wp_actionscheduler_logs", true)]
    [InlineData("wp_posts", false)]
    [InlineData("wp_users", false)]
    [InlineData("actionscheduler_actions", true)]
    public void IsActionSchedulerTable_VariousTables_ReturnsExpected(string tableName, bool expected)
    {
        var table = MakeTable(tableName);

        var result = table.IsActionSchedulerTable();

        result.Should().Be(expected);
    }

    [Fact]
    public void IsActionSchedulerTable_CaseInsensitive_ReturnsTrue()
    {
        var table = MakeTable("WP_ACTIONSCHEDULER_ACTIONS");

        var result = table.IsActionSchedulerTable();

        result.Should().BeTrue();
    }

    #endregion

    #region GetWordPressPrefix

    [Theory]
    [InlineData("wp_posts", "wp_")]
    [InlineData("wp_users", "wp_")]
    [InlineData("blog_postmeta", "blog_")]
    [InlineData("custom_terms", "custom_")]
    [InlineData("posts", "")]
    [InlineData("wp_", "wp_")]
    public void GetWordPressPrefix_VariousTables_ReturnsExpected(string tableName, string expected)
    {
        var table = MakeTable(tableName);

        var result = table.GetWordPressPrefix();

        result.Should().Be(expected);
    }

    #endregion
}
