using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model.AppSchema;

public class WordPressDetectorTests
{
    private readonly WordPressDetector _detector = new();

    #region Helper

    private static IReadOnlyList<IDbTable> MakeTables(params string[] tableNames)
        => tableNames.Select(name => MakeTable(name, "dbo")).ToList();

    private static IReadOnlyList<IDbTable> MakeTablesWithSchema(string schema, params string[] tableNames)
        => tableNames.Select(name => MakeTable(name, schema)).ToList();

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

    private static readonly string[] StandardWpTables =
    {
        "wp_users", "wp_usermeta", "wp_posts", "wp_postmeta",
        "wp_comments", "wp_commentmeta", "wp_options",
        "wp_terms", "wp_termmeta", "wp_term_taxonomy", "wp_term_relationships", "wp_links"
    };

    #endregion

    #region Detection

    [Fact]
    public void Detect_StandardWordPress_DetectsWpPrefix()
    {
        var tables = MakeTables(StandardWpTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.AppName.Should().Be("wordpress");
        result.PrefixGroups.Should().ContainSingle();
        result.PrefixGroups[0].Prefix.Should().Be("wp_");
        result.PrefixGroups[0].GroupName.Should().Be("wp");
        result.PrefixGroups[0].TableDbNames.Should().Contain("wp_users");
        result.PrefixGroups[0].TableDbNames.Should().Contain("wp_posts");
        result.PrefixGroups[0].TableDbNames.Should().Contain("wp_options");
    }

    [Fact]
    public void Detect_MultiSite_DetectsMultiplePrefixes()
    {
        var tableNames = StandardWpTables.Concat(new[]
        {
            "wp2_users", "wp2_posts", "wp2_options", "wp2_postmeta", "wp2_comments"
        }).ToArray();
        var tables = MakeTables(tableNames);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.PrefixGroups.Should().HaveCount(2);
        result.PrefixGroups.Should().Contain(g => g.Prefix == "wp_");
        result.PrefixGroups.Should().Contain(g => g.Prefix == "wp2_");

        var wp2Group = result.PrefixGroups.First(g => g.Prefix == "wp2_");
        wp2Group.GroupName.Should().Be("wp2");
        wp2Group.TableDbNames.Should().Contain("wp2_users");
        wp2Group.TableDbNames.Should().Contain("wp2_posts");
    }

    [Fact]
    public void Detect_CustomPrefix_DetectsBlogPrefix()
    {
        var tables = MakeTables("blog_users", "blog_posts", "blog_options", "blog_comments");

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.PrefixGroups.Should().ContainSingle();
        result.PrefixGroups[0].Prefix.Should().Be("blog_");
        result.PrefixGroups[0].GroupName.Should().Be("blog");
    }

    [Fact]
    public void Detect_MissingSignatureTable_ReturnsNull()
    {
        // Has users and posts but no options
        var tables = MakeTables("wp_users", "wp_posts", "wp_postmeta", "wp_comments");

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().BeNull();
    }

    [Fact]
    public void Detect_ConflictingSchemaName_BailsOut()
    {
        var tables = MakeTables(StandardWpTables);

        var result = _detector.Detect(tables, new[] { "wp" });

        result.Should().BeNull();
    }

    [Fact]
    public void Detect_ConflictingSchemaSkipsOnlyConflicting()
    {
        // wp_ conflicts, but wp2_ does not
        var tableNames = StandardWpTables.Concat(new[]
        {
            "wp2_users", "wp2_posts", "wp2_options"
        }).ToArray();
        var tables = MakeTables(tableNames);

        var result = _detector.Detect(tables, new[] { "wp" });

        result.Should().NotBeNull();
        result!.PrefixGroups.Should().ContainSingle();
        result.PrefixGroups[0].Prefix.Should().Be("wp2_");
    }

    [Fact]
    public void Detect_EmptyTables_ReturnsNull()
    {
        var result = _detector.Detect(Array.Empty<IDbTable>(), Array.Empty<string>());
        result.Should().BeNull();
    }

    [Fact]
    public void Detect_NoPrefix_ReturnsNull()
    {
        // Tables without a common prefix pattern
        var tables = MakeTables("users", "posts", "options");

        // "users" doesn't have an underscore before "users" so no prefix extracted
        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().BeNull();
    }

    #endregion

    #region Explicit Foreign Keys

    [Fact]
    public void Detect_GeneratesExplicitFKs()
    {
        var tables = MakeTables(StandardWpTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var fks = result!.ExplicitForeignKeys;

        // All 10 WordPress FK entries should be present with prefixed names
        fks.Should().Contain(fk => fk.ChildTable == "wp_posts" && fk.ChildColumn == "post_author" && fk.ParentTable == "wp_users");
        fks.Should().Contain(fk => fk.ChildTable == "wp_posts" && fk.ChildColumn == "post_parent" && fk.ParentTable == "wp_posts");
        fks.Should().Contain(fk => fk.ChildTable == "wp_postmeta" && fk.ChildColumn == "post_id" && fk.ParentTable == "wp_posts");
        fks.Should().Contain(fk => fk.ChildTable == "wp_usermeta" && fk.ChildColumn == "user_id" && fk.ParentTable == "wp_users");
        fks.Should().Contain(fk => fk.ChildTable == "wp_comments" && fk.ChildColumn == "comment_post_ID" && fk.ParentTable == "wp_posts");
        fks.Should().Contain(fk => fk.ChildTable == "wp_comments" && fk.ChildColumn == "user_id" && fk.ParentTable == "wp_users");
        fks.Should().Contain(fk => fk.ChildTable == "wp_commentmeta" && fk.ChildColumn == "comment_id" && fk.ParentTable == "wp_comments");
        fks.Should().Contain(fk => fk.ChildTable == "wp_termmeta" && fk.ChildColumn == "term_id" && fk.ParentTable == "wp_terms");
        fks.Should().Contain(fk => fk.ChildTable == "wp_term_taxonomy" && fk.ChildColumn == "term_id" && fk.ParentTable == "wp_terms");
        fks.Should().Contain(fk => fk.ChildTable == "wp_term_relationships" && fk.ChildColumn == "term_taxonomy_id" && fk.ParentTable == "wp_term_taxonomy");
    }

    [Fact]
    public void Detect_FKsOnlyForExistingTables()
    {
        // Only users and posts exist (plus options for detection) — no comments table
        var tables = MakeTables("wp_users", "wp_posts", "wp_options");

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        // FK for comments -> posts should NOT be present since wp_comments doesn't exist
        result!.ExplicitForeignKeys.Should().NotContain(fk => fk.ChildTable == "wp_comments");
        // Self-referential posts FK should still be present
        result.ExplicitForeignKeys.Should().Contain(fk => fk.ChildTable == "wp_posts" && fk.ChildColumn == "post_parent");
    }

    [Fact]
    public void Detect_FKsUsePrefixedNames()
    {
        var tables = MakeTables(StandardWpTables);
        var result = _detector.Detect(tables, Array.Empty<string>());

        // All FK entries should use prefixed table names
        foreach (var fk in result!.ExplicitForeignKeys)
        {
            fk.ChildTable.Should().StartWith("wp_");
            fk.ParentTable.Should().StartWith("wp_");
        }
    }

    #endregion

    #region Metadata: Hidden Tables

    [Fact]
    public void Detect_HidesActionSchedulerTables()
    {
        var tableNames = StandardWpTables.Concat(new[]
        {
            "wp_actionscheduler_actions", "wp_actionscheduler_claims",
            "wp_actionscheduler_groups", "wp_actionscheduler_logs"
        }).ToArray();
        var tables = MakeTables(tableNames);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var meta = result!.AdditionalMetadata;

        meta.Should().ContainKey("dbo.wp_actionscheduler_actions");
        meta["dbo.wp_actionscheduler_actions"]["visibility"].Should().Be("hidden");
        meta.Should().ContainKey("dbo.wp_actionscheduler_claims");
        meta["dbo.wp_actionscheduler_claims"]["visibility"].Should().Be("hidden");
        meta.Should().ContainKey("dbo.wp_actionscheduler_groups");
        meta["dbo.wp_actionscheduler_groups"]["visibility"].Should().Be("hidden");
        meta.Should().ContainKey("dbo.wp_actionscheduler_logs");
        meta["dbo.wp_actionscheduler_logs"]["visibility"].Should().Be("hidden");
    }

    [Fact]
    public void Detect_NoActionSchedulerTables_NoHiddenMetadata()
    {
        var tables = MakeTables(StandardWpTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.AdditionalMetadata.Keys.Should().NotContain(k => k.Contains("actionscheduler"));
    }

    #endregion

    #region Metadata: Labels

    [Fact]
    public void Detect_InjectsLabels()
    {
        var tables = MakeTables(StandardWpTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var meta = result!.AdditionalMetadata;

        meta.Should().ContainKey("dbo.wp_posts");
        meta["dbo.wp_posts"]["label"].Should().Be("Posts");

        meta.Should().ContainKey("dbo.wp_users");
        meta["dbo.wp_users"]["label"].Should().Be("Users");

        meta.Should().ContainKey("dbo.wp_comments");
        meta["dbo.wp_comments"]["label"].Should().Be("Comments");

        meta.Should().ContainKey("dbo.wp_options");
        meta["dbo.wp_options"]["label"].Should().Be("Options");

        meta.Should().ContainKey("dbo.wp_terms");
        meta["dbo.wp_terms"]["label"].Should().Be("Terms");
    }

    [Fact]
    public void Detect_LabelsUseCorrectSchema()
    {
        var tables = MakeTablesWithSchema("mydb", StandardWpTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        // Keys should use the actual table schema
        result!.AdditionalMetadata.Should().ContainKey("mydb.wp_posts");
        result.AdditionalMetadata["mydb.wp_posts"]["label"].Should().Be("Posts");
    }

    #endregion

    #region IsEnabled

    [Fact]
    public void IsEnabled_DefaultTrue()
    {
        var metadata = new Dictionary<string, object?>();
        _detector.IsEnabled(metadata).Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_DisabledByMetadata()
    {
        var metadata = new Dictionary<string, object?> { ["auto-detect-app"] = "disabled" };
        _detector.IsEnabled(metadata).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_CaseInsensitive()
    {
        var metadata = new Dictionary<string, object?> { ["auto-detect-app"] = "Disabled" };
        _detector.IsEnabled(metadata).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_OtherValueStillEnabled()
    {
        var metadata = new Dictionary<string, object?> { ["auto-detect-app"] = "wordpress" };
        _detector.IsEnabled(metadata).Should().BeTrue();
    }

    #endregion

    #region Prefix Group Completeness

    [Fact]
    public void Detect_CollectsAllPrefixedTables()
    {
        var tables = MakeTables(StandardWpTables);
        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var group = result!.PrefixGroups[0];
        group.TableDbNames.Should().HaveCount(StandardWpTables.Length);
        foreach (var name in StandardWpTables)
            group.TableDbNames.Should().Contain(name);
    }

    #endregion
}
