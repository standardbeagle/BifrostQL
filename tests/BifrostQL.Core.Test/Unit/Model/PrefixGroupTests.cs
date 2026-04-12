using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

public class PrefixGroupTests
{
    [Fact]
    public void DetectPrefixGroups_FindsWordPressPrefix()
    {
        var model = CreateWordPressModel();
        var groups = DbModel.DetectPrefixGroups(model.Tables);

        groups.Should().ContainSingle(g => g.Prefix == "wp_");
        var wpGroup = groups.First(g => g.Prefix == "wp_");
        wpGroup.GroupName.Should().Be("wp");
        wpGroup.TableDbNames.Should().Contain("wp_users");
        wpGroup.TableDbNames.Should().Contain("wp_posts");
        wpGroup.TableDbNames.Should().Contain("wp_usermeta");
    }

    [Fact]
    public void DetectPrefixGroups_MultipleGroups()
    {
        var fixture = DbModelTestFixture.Create()
            .WithTable("wp_users", t => t.WithSchema("dbo").WithPrimaryKey("ID", "int"))
            .WithTable("wp_posts", t => t.WithSchema("dbo").WithPrimaryKey("ID", "int"))
            .WithTable("wp_comments", t => t.WithSchema("dbo").WithPrimaryKey("comment_ID", "int"))
            .WithTable("wp2_users", t => t.WithSchema("dbo").WithPrimaryKey("ID", "int"))
            .WithTable("wp2_posts", t => t.WithSchema("dbo").WithPrimaryKey("ID", "int"))
            .WithTable("wp2_options", t => t.WithSchema("dbo").WithPrimaryKey("option_id", "int"))
            .Build();

        var groups = DbModel.DetectPrefixGroups(fixture.Tables);

        groups.Should().Contain(g => g.Prefix == "wp_");
        groups.Should().Contain(g => g.Prefix == "wp2_");
        groups.First(g => g.Prefix == "wp_").TableDbNames.Should().HaveCountGreaterThanOrEqualTo(3);
        groups.First(g => g.Prefix == "wp2_").TableDbNames.Should().HaveCount(3);
    }

    [Fact]
    public void DetectPrefixGroups_NoPrefixWhenFewerThanThreeTables()
    {
        var fixture = DbModelTestFixture.Create()
            .WithTable("wp_users", t => t.WithSchema("dbo").WithPrimaryKey("ID", "int"))
            .WithTable("wp_posts", t => t.WithSchema("dbo").WithPrimaryKey("ID", "int"))
            .WithTable("other_table", t => t.WithSchema("dbo").WithPrimaryKey("Id", "int"))
            .Build();

        var groups = DbModel.DetectPrefixGroups(fixture.Tables);

        groups.Should().NotContain(g => g.Prefix == "wp_");
    }

    [Fact]
    public void LinkTablesFromNames_WithPrefixGroup_MatchesStrippedNames()
    {
        // wp_usermeta has user_id column. Without prefix awareness, "user" doesn't match "wp_user".
        // With prefix groups, it should match wp_users within the group.
        var model = DbModelTestFixture.Create()
            .WithTable("wp_users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("ID", "int")
                .WithColumn("user_login", "nvarchar"))
            .WithTable("wp_usermeta", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("umeta_id", "int")
                .WithColumn("user_id", "int")
                .WithColumn("meta_key", "nvarchar"))
            .WithTable("wp_posts", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("ID", "int")
                .WithColumn("post_author", "nvarchar"))
            .WithForeignKey("dummy", "dbo", "force_build_path", new[] { "x" }, "dbo", "force_build_path", new[] { "x" })
            .Build();

        // The model goes through FromTables which auto-detects prefix groups.
        // wp_usermeta.user_id should link to wp_users.
        var usermeta = model.GetTableFromDbName("wp_usermeta");
        usermeta.SingleLinks.Should().ContainKey("wp_users",
            "user_id column should match wp_users table within wp_ prefix group");
    }

    [Fact]
    public void LinkTablesFromNames_WithPrefixGroup_ScopesToGroup()
    {
        // wp_usermeta.user_id should match wp_users (same prefix group), NOT wp2_users.
        var model = DbModelTestFixture.Create()
            .WithTable("wp_users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("ID", "int")
                .WithColumn("user_login", "nvarchar"))
            .WithTable("wp_usermeta", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("umeta_id", "int")
                .WithColumn("user_id", "int"))
            .WithTable("wp_posts", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("ID", "int")
                .WithColumn("post_title", "nvarchar"))
            .WithTable("wp2_users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("ID", "int")
                .WithColumn("user_login", "nvarchar"))
            .WithTable("wp2_posts", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("ID", "int")
                .WithColumn("post_title", "nvarchar"))
            .WithTable("wp2_options", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("option_id", "int")
                .WithColumn("option_name", "nvarchar"))
            .WithForeignKey("dummy", "dbo", "force_build_path", new[] { "x" }, "dbo", "force_build_path", new[] { "x" })
            .Build();

        var usermeta = model.GetTableFromDbName("wp_usermeta");
        usermeta.SingleLinks.Should().ContainKey("wp_users");
        usermeta.SingleLinks["wp_users"].ParentTable.DbName.Should().Be("wp_users");
    }

    [Fact]
    public void LinkTablesFromNames_NoPrefixGroup_ExistingBehavior()
    {
        // Standard non-prefixed tables should still auto-link as before.
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id", "int")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id", "int")
                .WithColumn("UserId", "int")
                .WithColumn("Total", "decimal"))
            .WithForeignKey("dummy", "dbo", "force_build_path", new[] { "x" }, "dbo", "force_build_path", new[] { "x" })
            .Build();

        var orders = model.GetTableFromDbName("Orders");
        orders.SingleLinks.Should().ContainKey("Users");
    }

    [Fact]
    public void ParsePrefixGroups_FromMetadata()
    {
        var fixture = DbModelTestFixture.Create()
            .WithTable("wp_users", t => t.WithSchema("dbo").WithPrimaryKey("ID", "int"))
            .WithTable("wp_posts", t => t.WithSchema("dbo").WithPrimaryKey("ID", "int"))
            .WithTable("wp2_users", t => t.WithSchema("dbo").WithPrimaryKey("ID", "int"))
            .Build();

        var groups = DbModel.ParsePrefixGroups("wp_=wp, wp2_=wp2", fixture.Tables);

        groups.Should().HaveCount(2);
        groups.Should().Contain(g => g.Prefix == "wp_" && g.GroupName == "wp");
        groups.Should().Contain(g => g.Prefix == "wp2_" && g.GroupName == "wp2");

        var wpGroup = groups.First(g => g.Prefix == "wp_");
        wpGroup.TableDbNames.Should().Contain("wp_users");
        wpGroup.TableDbNames.Should().Contain("wp_posts");
        wpGroup.TableDbNames.Should().NotContain("wp2_users");
    }

    [Fact]
    public void ParsePrefixGroups_AddsUnderscoreIfMissing()
    {
        var fixture = DbModelTestFixture.Create()
            .WithTable("wp_users", t => t.WithSchema("dbo").WithPrimaryKey("ID", "int"))
            .WithTable("wp_posts", t => t.WithSchema("dbo").WithPrimaryKey("ID", "int"))
            .Build();

        var groups = DbModel.ParsePrefixGroups("wp=wordpress", fixture.Tables);

        groups.Should().ContainSingle();
        groups[0].Prefix.Should().Be("wp_");
        groups[0].GroupName.Should().Be("wordpress");
    }

    private static IDbModel CreateWordPressModel()
    {
        return DbModelTestFixture.Create()
            .WithTable("wp_users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("ID", "int")
                .WithColumn("user_login", "nvarchar"))
            .WithTable("wp_usermeta", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("umeta_id", "int")
                .WithColumn("user_id", "int")
                .WithColumn("meta_key", "nvarchar"))
            .WithTable("wp_posts", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("ID", "int")
                .WithColumn("post_author", "int")
                .WithColumn("post_title", "nvarchar"))
            .WithTable("wp_postmeta", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("meta_id", "int")
                .WithColumn("post_id", "int")
                .WithColumn("meta_key", "nvarchar"))
            .WithTable("wp_comments", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("comment_ID", "int")
                .WithColumn("comment_post_ID", "int")
                .WithColumn("user_id", "int"))
            .Build();
    }
}
