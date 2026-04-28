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
        result.Confidence.Should().BeGreaterThan(0.5);
        result.SchemaResult.PrefixGroups.Should().ContainSingle();
        result.SchemaResult.PrefixGroups[0].Prefix.Should().Be("wp_");
        result.SchemaResult.PrefixGroups[0].GroupName.Should().Be("wp");
        result.SchemaResult.PrefixGroups[0].TableDbNames.Should().Contain("wp_users");
        result.SchemaResult.PrefixGroups[0].TableDbNames.Should().Contain("wp_posts");
        result.SchemaResult.PrefixGroups[0].TableDbNames.Should().Contain("wp_options");
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
        result!.SchemaResult.PrefixGroups.Should().HaveCount(2);
        result.SchemaResult.PrefixGroups.Should().Contain(g => g.Prefix == "wp_");
        result.SchemaResult.PrefixGroups.Should().Contain(g => g.Prefix == "wp2_");

        var wp2Group = result.SchemaResult.PrefixGroups.First(g => g.Prefix == "wp2_");
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
        result!.SchemaResult.PrefixGroups.Should().ContainSingle();
        result.SchemaResult.PrefixGroups[0].Prefix.Should().Be("blog_");
        result.SchemaResult.PrefixGroups[0].GroupName.Should().Be("blog");
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
        result!.SchemaResult.PrefixGroups.Should().ContainSingle();
        result.SchemaResult.PrefixGroups[0].Prefix.Should().Be("wp2_");
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

    #region Confidence Scoring

    [Fact]
    public void Detect_StandardWordPress_HasHighConfidence()
    {
        var tables = MakeTables(StandardWpTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.Confidence.Should().BeGreaterThan(0.8);
    }

    [Fact]
    public void Detect_MinimalTables_HasBaseConfidence()
    {
        // Only signature tables - minimum for detection
        var tables = MakeTables("wp_users", "wp_posts", "wp_options");

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        // Should have base confidence of ~0.6
        result!.Confidence.Should().BeGreaterThanOrEqualTo(0.6);
        result.Confidence.Should().BeLessThan(0.7);
    }

    [Fact]
    public void Detect_Multisite_HasHigherConfidence()
    {
        var tableNames = StandardWpTables.Concat(new[]
        {
            "wp2_users", "wp2_posts", "wp2_options", "wp2_postmeta", "wp2_usermeta", "wp2_comments"
        }).ToArray();
        var tables = MakeTables(tableNames);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        // Multisite should have slightly higher confidence due to bonus
        result!.Confidence.Should().BeGreaterThan(0.8);
    }

    [Fact]
    public void Detect_ConfidenceClampedToMaximum()
    {
        // All tables present - should not exceed 1.0
        var allTables = StandardWpTables.Concat(new[]
        {
            "wp_actionscheduler_actions", "wp_actionscheduler_claims"
        }).ToArray();
        var tables = MakeTables(allTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.Confidence.Should().BeLessThanOrEqualTo(1.0);
    }

    #endregion

    #region Explicit Foreign Keys

    [Fact]
    public void Detect_GeneratesExplicitFKs()
    {
        var tables = MakeTables(StandardWpTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var fks = result!.SchemaResult.ExplicitForeignKeys;

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
        result!.SchemaResult.ExplicitForeignKeys.Should().NotContain(fk => fk.ChildTable == "wp_comments");
        // Self-referential posts FK should still be present
        result.SchemaResult.ExplicitForeignKeys.Should().Contain(fk => fk.ChildTable == "wp_posts" && fk.ChildColumn == "post_parent");
    }

    [Fact]
    public void Detect_FKsUsePrefixedNames()
    {
        var tables = MakeTables(StandardWpTables);
        var result = _detector.Detect(tables, Array.Empty<string>());

        // All FK entries should use prefixed table names
        foreach (var fk in result!.SchemaResult.ExplicitForeignKeys)
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
        var meta = result!.SchemaResult.AdditionalMetadata;

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
        result!.SchemaResult.AdditionalMetadata.Keys.Should().NotContain(k => k.Contains("actionscheduler"));
    }

    #endregion

    #region Metadata: Labels

    [Fact]
    public void Detect_InjectsLabels()
    {
        var tables = MakeTables(StandardWpTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var meta = result!.SchemaResult.AdditionalMetadata;

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
        result!.SchemaResult.AdditionalMetadata.Should().ContainKey("mydb.wp_posts");
        result.SchemaResult.AdditionalMetadata["mydb.wp_posts"]["label"].Should().Be("Posts");
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
        var group = result!.SchemaResult.PrefixGroups[0];
        group.TableDbNames.Should().HaveCount(StandardWpTables.Length);
        foreach (var name in StandardWpTables)
            group.TableDbNames.Should().Contain(name);
    }

    #endregion

    #region Serialized PHP Column Detection

    private static IDbTable MakeTableWithColumns(string dbName, string schema, params (string Name, string DataType)[] columns)
    {
        var columnDtos = columns.Select(c => new ColumnDto
        {
            TableSchema = schema,
            TableName = dbName,
            ColumnName = c.Name,
            GraphQlName = c.Name,
            NormalizedName = c.Name,
            DataType = c.DataType,
            IsNullable = true,
            Metadata = new Dictionary<string, object?>()
        }).ToList();

        return new DbTable
        {
            DbName = dbName,
            GraphQlName = dbName,
            NormalizedName = dbName,
            TableSchema = schema,
            TableType = "BASE TABLE",
            ColumnLookup = columnDtos.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase),
            GraphQlLookup = columnDtos.ToDictionary(c => c.GraphQlName, StringComparer.OrdinalIgnoreCase),
            SingleLinks = new Dictionary<string, TableLinkDto>(),
            MultiLinks = new Dictionary<string, TableLinkDto>(),
            ManyToManyLinks = new Dictionary<string, ManyToManyLink>(),
        };
    }

    [Fact]
    public void Detect_MetaValueColumns_MarkedAsPhpSerialized()
    {
        var tables = new List<IDbTable>
        {
            MakeTableWithColumns("wp_posts", "dbo", ("ID", "int"), ("post_title", "nvarchar")),
            MakeTableWithColumns("wp_postmeta", "dbo", ("meta_id", "int"), ("post_id", "int"), ("meta_key", "nvarchar"), ("meta_value", "longtext")),
            MakeTableWithColumns("wp_users", "dbo", ("ID", "int"), ("user_login", "nvarchar")),
            MakeTableWithColumns("wp_usermeta", "dbo", ("umeta_id", "int"), ("user_id", "int"), ("meta_key", "nvarchar"), ("meta_value", "longtext")),
            MakeTableWithColumns("wp_options", "dbo", ("option_id", "int"), ("option_name", "nvarchar"), ("option_value", "longtext")),
        };

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.SchemaResult.ColumnMetadata.Should().ContainKey("dbo.wp_postmeta.meta_value");
        result.SchemaResult.ColumnMetadata["dbo.wp_postmeta.meta_value"]["type"].Should().Be("php_serialized");
        result.SchemaResult.ColumnMetadata["dbo.wp_postmeta.meta_value"]["format"].Should().Be("php");

        result.SchemaResult.ColumnMetadata.Should().ContainKey("dbo.wp_usermeta.meta_value");
        result.SchemaResult.ColumnMetadata["dbo.wp_usermeta.meta_value"]["type"].Should().Be("php_serialized");

        result.SchemaResult.ColumnMetadata.Should().ContainKey("dbo.wp_options.option_value");
        result.SchemaResult.ColumnMetadata["dbo.wp_options.option_value"]["type"].Should().Be("php_serialized");
    }

    [Fact]
    public void Detect_CustomPrefix_SerializedColumnsMarked()
    {
        var tables = new List<IDbTable>
        {
            MakeTableWithColumns("blog_posts", "dbo", ("ID", "int"), ("post_title", "nvarchar")),
            MakeTableWithColumns("blog_postmeta", "dbo", ("meta_id", "int"), ("post_id", "int"), ("meta_key", "nvarchar"), ("meta_value", "longtext")),
            MakeTableWithColumns("blog_users", "dbo", ("ID", "int"), ("user_login", "nvarchar")),
            MakeTableWithColumns("blog_usermeta", "dbo", ("umeta_id", "int"), ("user_id", "int"), ("meta_key", "nvarchar"), ("meta_value", "longtext")),
            MakeTableWithColumns("blog_options", "dbo", ("option_id", "int"), ("option_name", "nvarchar"), ("option_value", "longtext")),
        };

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.SchemaResult.ColumnMetadata.Should().ContainKey("dbo.blog_postmeta.meta_value");
        result.SchemaResult.ColumnMetadata["dbo.blog_postmeta.meta_value"]["type"].Should().Be("php_serialized");
        result.SchemaResult.ColumnMetadata.Should().ContainKey("dbo.blog_options.option_value");
        result.SchemaResult.ColumnMetadata["dbo.blog_options.option_value"]["type"].Should().Be("php_serialized");
    }

    [Fact]
    public void Detect_MissingColumn_NoMetadataForMissingColumn()
    {
        // Table exists but without the meta_value column
        var tables = new List<IDbTable>
        {
            MakeTableWithColumns("wp_posts", "dbo", ("ID", "int")),
            MakeTableWithColumns("wp_postmeta", "dbo", ("meta_id", "int"), ("post_id", "int")), // no meta_value
            MakeTableWithColumns("wp_users", "dbo", ("ID", "int")),
            MakeTableWithColumns("wp_options", "dbo", ("option_id", "int"), ("option_name", "nvarchar"), ("option_value", "longtext")),
        };

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        // Should not contain wp_postmeta.meta_value since the column doesn't exist
        result!.SchemaResult.ColumnMetadata.Should().NotContainKey("dbo.wp_postmeta.meta_value");
        // But wp_options.option_value should still be marked
        result.SchemaResult.ColumnMetadata.Should().ContainKey("dbo.wp_options.option_value");
    }

    [Fact]
    public void Detect_Multisite_SerializedColumnsMarkedForAllPrefixes()
    {
        var tables = new List<IDbTable>
        {
            MakeTableWithColumns("wp_posts", "dbo", ("ID", "int")),
            MakeTableWithColumns("wp_postmeta", "dbo", ("meta_id", "int"), ("post_id", "int"), ("meta_key", "nvarchar"), ("meta_value", "longtext")),
            MakeTableWithColumns("wp_users", "dbo", ("ID", "int")),
            MakeTableWithColumns("wp_options", "dbo", ("option_id", "int"), ("option_name", "nvarchar"), ("option_value", "longtext")),
            MakeTableWithColumns("wp2_posts", "dbo", ("ID", "int")),
            MakeTableWithColumns("wp2_postmeta", "dbo", ("meta_id", "int"), ("post_id", "int"), ("meta_key", "nvarchar"), ("meta_value", "longtext")),
            MakeTableWithColumns("wp2_users", "dbo", ("ID", "int")),
            MakeTableWithColumns("wp2_options", "dbo", ("option_id", "int"), ("option_name", "nvarchar"), ("option_value", "longtext")),
        };

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.SchemaResult.ColumnMetadata.Should().ContainKey("dbo.wp_postmeta.meta_value");
        result.SchemaResult.ColumnMetadata.Should().ContainKey("dbo.wp_options.option_value");
        result.SchemaResult.ColumnMetadata.Should().ContainKey("dbo.wp2_postmeta.meta_value");
        result.SchemaResult.ColumnMetadata.Should().ContainKey("dbo.wp2_options.option_value");
    }

    [Fact]
    public void Detect_TermmetaAndCommentmeta_SerializedColumnsMarked()
    {
        var tables = new List<IDbTable>
        {
            MakeTableWithColumns("wp_posts", "dbo", ("ID", "int")),
            MakeTableWithColumns("wp_users", "dbo", ("ID", "int")),
            MakeTableWithColumns("wp_options", "dbo", ("option_id", "int")),
            MakeTableWithColumns("wp_terms", "dbo", ("term_id", "int")),
            MakeTableWithColumns("wp_termmeta", "dbo", ("meta_id", "int"), ("term_id", "int"), ("meta_key", "nvarchar"), ("meta_value", "longtext")),
            MakeTableWithColumns("wp_comments", "dbo", ("comment_ID", "int")),
            MakeTableWithColumns("wp_commentmeta", "dbo", ("meta_id", "int"), ("comment_id", "int"), ("meta_key", "nvarchar"), ("meta_value", "longtext")),
        };

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.SchemaResult.ColumnMetadata.Should().ContainKey("dbo.wp_termmeta.meta_value");
        result.SchemaResult.ColumnMetadata["dbo.wp_termmeta.meta_value"]["type"].Should().Be("php_serialized");
        result.SchemaResult.ColumnMetadata.Should().ContainKey("dbo.wp_commentmeta.meta_value");
        result.SchemaResult.ColumnMetadata["dbo.wp_commentmeta.meta_value"]["type"].Should().Be("php_serialized");
    }

    #endregion

    #region Case-Sensitive Prefix Detection

    [Fact]
    public void Detect_MixedCasePrefixes_MergesUnderMostCommon()
    {
        // wp_ has more tables, wP_ has fewer — should merge under wp_
        var tableNames = new[]
        {
            "wp_users", "wp_posts", "wp_options", "wp_comments", "wp_postmeta",
            "wP_users", "wP_posts", "wP_options"
        };
        var tables = MakeTables(tableNames);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.SchemaResult.PrefixGroups.Should().ContainSingle();
        result.SchemaResult.PrefixGroups[0].Prefix.Should().Be("wp_");
        result.SchemaResult.PrefixGroups[0].GroupName.Should().Be("wp");
        // Only wp_-prefixed tables should be in the group
        result.SchemaResult.PrefixGroups[0].TableDbNames.Should().Contain("wp_users");
        result.SchemaResult.PrefixGroups[0].TableDbNames.Should().NotContain("wP_users");
    }

    [Fact]
    public void Detect_MixedCasePrefixes_PicksVariantWithMostTables()
    {
        // wP_ has more tables than wp_ — should merge under wP_
        var tableNames = new[]
        {
            "wp_users", "wp_posts", "wp_options",
            "wP_users", "wP_posts", "wP_options", "wP_comments", "wP_postmeta", "wP_usermeta"
        };
        var tables = MakeTables(tableNames);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.SchemaResult.PrefixGroups.Should().ContainSingle();
        result.SchemaResult.PrefixGroups[0].Prefix.Should().Be("wP_");
        result.SchemaResult.PrefixGroups[0].GroupName.Should().Be("wP");
    }

    [Fact]
    public void Detect_MixedCasePrefixes_DoesNotCauseDuplicateGroups()
    {
        // Both wp_ and wP_ are valid — must not produce two groups with conflicting names
        var tableNames = new[]
        {
            "wp_users", "wp_posts", "wp_options",
            "wP_users", "wP_posts", "wP_options"
        };
        var tables = MakeTables(tableNames);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        // Must have exactly one group, not two conflicting ones
        result!.SchemaResult.PrefixGroups.Should().ContainSingle();
    }

    [Fact]
    public void Detect_DifferentCasePrefixesWithDifferentGroupNames_KeepsBoth()
    {
        // wp_ and Blog_ produce different group names — both should be kept
        var tableNames = new[]
        {
            "wp_users", "wp_posts", "wp_options",
            "Blog_users", "Blog_posts", "Blog_options"
        };
        var tables = MakeTables(tableNames);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.SchemaResult.PrefixGroups.Should().HaveCount(2);
        result.SchemaResult.PrefixGroups.Should().Contain(g => g.Prefix == "wp_");
        result.SchemaResult.PrefixGroups.Should().Contain(g => g.Prefix == "Blog_");
    }

    [Fact]
    public void Detect_CaseSensitiveGroupTableCollection()
    {
        // wp_ group should only contain wp_-prefixed tables, not wP_-prefixed
        var tableNames = new[]
        {
            "wp_users", "wp_posts", "wp_options", "wp_comments",
            "wP_users", "wP_posts", "wP_options"
        };
        var tables = MakeTables(tableNames);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var group = result!.SchemaResult.PrefixGroups[0];
        group.Prefix.Should().Be("wp_");
        group.TableDbNames.Should().HaveCount(4); // only wp_ tables
        group.TableDbNames.Should().NotContain(t => t.StartsWith("wP_"));
    }

    [Fact]
    public void Detect_MixedCasePrefixes_FKsUseWinningPrefix()
    {
        var tableNames = new[]
        {
            "wp_users", "wp_posts", "wp_options", "wp_postmeta",
            "wP_users", "wP_posts", "wP_options"
        };
        var tables = MakeTables(tableNames);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        // FKs should only reference wp_-prefixed tables
        foreach (var fk in result!.SchemaResult.ExplicitForeignKeys)
        {
            fk.ChildTable.Should().StartWith("wp_");
            fk.ParentTable.Should().StartWith("wp_");
        }
    }

    #endregion
}
