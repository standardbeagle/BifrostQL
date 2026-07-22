using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

public class FeedConfigTests
{
    private static DbModelTestFixture.TableBuilder FeedTable(DbModelTestFixture.TableBuilder table)
        => table.WithSchema("dbo").WithPrimaryKey("id")
            .WithColumn("published_at", "datetime2")
            .WithColumn("title", "nvarchar")
            .WithColumn("body", "nvarchar")
            .WithColumn("slug", "nvarchar")
            .WithMetadata(MetadataKeys.Feed.Timestamp, " PUBLISHED_AT ")
            .WithMetadata(MetadataKeys.Feed.Title, "Post: {title}")
            .WithMetadata(MetadataKeys.Feed.Body, "body")
            .WithMetadata(MetadataKeys.Feed.Link, "/posts/{slug}");

    [Fact]
    public void FromTable_WithoutTimestamp_IsNotOptedIn()
    {
        var table = DbModelTestFixture.Create()
            .WithTable("posts", t => t.WithSchema("dbo").WithPrimaryKey("id"))
            .Build().Tables.Single();

        FeedConfig.FromTable(table).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Validate_ValidFeedWithNormalizedTimestamp_DoesNotThrow()
    {
        var model = DbModelTestFixture.Create().WithTable("posts", FeedTable).Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing")]
    [InlineData("title")]
    public void Validate_InvalidTimestamp_Throws(string? timestamp)
    {
        var model = DbModelTestFixture.Create().WithTable("posts", t =>
        {
            FeedTable(t);
            t.WithMetadata(MetadataKeys.Feed.Timestamp, timestamp);
        }).Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain(MetadataKeys.Feed.Timestamp);
    }

    [Fact]
    public void Validate_MissingOrEncryptedFeedColumns_Throw()
    {
        var model = DbModelTestFixture.Create().WithTable("posts", t =>
        {
            FeedTable(t);
            t.WithMetadata(MetadataKeys.Feed.Body, "missing");
            t.WithColumnMetadata("title", MetadataKeys.Crypto.Encrypt, "aes-256-gcm");
            t.WithColumnMetadata("title", MetadataKeys.Crypto.KeyRef, "kms:feed");
        }).Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain(MetadataKeys.Feed.Body).And.Contain(MetadataKeys.Feed.Title)
            .And.Contain(MetadataKeys.Crypto.Encrypt);
    }

    [Theory]
    [InlineData("Post: {missing}")]
    [InlineData("Post: {title")]
    [InlineData("Post: {}")]
    public void Validate_InvalidTitleTemplate_Throws(string title)
    {
        var model = DbModelTestFixture.Create().WithTable("posts", t =>
        {
            FeedTable(t);
            t.WithMetadata(MetadataKeys.Feed.Title, title);
        }).Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain(MetadataKeys.Feed.Title);
    }

    [Fact]
    public void Validate_StrayAndMiscasedFeedKeys_Throw()
    {
        var model = DbModelTestFixture.Create().WithTable("posts", t =>
        {
            FeedTable(t);
            t.WithMetadata("feed-author", "author");
            t.WithMetadata("Feed-Timestamp", "published_at");
        }).Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("feed-author").And.Contain("Feed-Timestamp");
    }

    [Fact]
    public void Validate_FeedMetadataWithoutTimestampAndCompositePrimaryKey_Throw()
    {
        var model = DbModelTestFixture.Create().WithTable("posts", t => t
            .WithSchema("dbo").WithPrimaryKey("tenant_id").WithPrimaryKey("id")
            .WithColumn("published_at", "datetime2")
            .WithColumn("title", "nvarchar")
            .WithMetadata(MetadataKeys.Feed.Title, "title")
            .WithMetadata(MetadataKeys.Feed.Body, "title"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain(MetadataKeys.Feed.Timestamp).And.Contain("composite primary key");
    }
}
