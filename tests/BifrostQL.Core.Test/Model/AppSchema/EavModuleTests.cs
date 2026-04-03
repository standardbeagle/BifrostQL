using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model.AppSchema;

public class EavModuleTests
{
    #region Helper

    private static IReadOnlyList<IDbTable> MakeTables(params string[] tableNames)
        => tableNames.Select(name => MakeTable(name, "dbo")).ToList();

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

    private static DbTable MakeTableWithMetadata(string dbName, string schema, IDictionary<string, object?> metadata)
    {
        var table = new DbTable
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
        foreach (var (key, value) in metadata)
            table.Metadata[key] = value;
        return table;
    }

    private static readonly string[] StandardWpTables =
    {
        "wp_users", "wp_usermeta", "wp_posts", "wp_postmeta",
        "wp_comments", "wp_commentmeta", "wp_options",
        "wp_terms", "wp_termmeta", "wp_term_taxonomy", "wp_term_relationships", "wp_links"
    };

    #endregion

    #region EavConfig Parsing from Metadata

    [Fact]
    public void CollectEavConfigs_AllKeysPresent_ProducesConfig()
    {
        var metaTable = MakeTableWithMetadata("wp_postmeta", "dbo", new Dictionary<string, object?>
        {
            ["eav-parent"] = "wp_posts",
            ["eav-fk"] = "post_id",
            ["eav-key"] = "meta_key",
            ["eav-value"] = "meta_value",
        });
        var parentTable = MakeTable("wp_posts");
        var tables = new IDbTable[] { parentTable, metaTable };

        var configs = DbModel.CollectEavConfigs(tables);

        configs.Should().ContainSingle();
        configs[0].MetaTableDbName.Should().Be("wp_postmeta");
        configs[0].ParentTableDbName.Should().Be("wp_posts");
        configs[0].ForeignKeyColumn.Should().Be("post_id");
        configs[0].KeyColumn.Should().Be("meta_key");
        configs[0].ValueColumn.Should().Be("meta_value");
    }

    [Fact]
    public void CollectEavConfigs_MissingKey_SkipsTable()
    {
        var metaTable = MakeTableWithMetadata("wp_postmeta", "dbo", new Dictionary<string, object?>
        {
            ["eav-parent"] = "wp_posts",
            ["eav-fk"] = "post_id",
            // missing eav-key and eav-value
        });
        var parentTable = MakeTable("wp_posts");
        var tables = new IDbTable[] { parentTable, metaTable };

        var configs = DbModel.CollectEavConfigs(tables);

        configs.Should().BeEmpty();
    }

    [Fact]
    public void CollectEavConfigs_ParentNotFound_SkipsTable()
    {
        var metaTable = MakeTableWithMetadata("wp_postmeta", "dbo", new Dictionary<string, object?>
        {
            ["eav-parent"] = "nonexistent_table",
            ["eav-fk"] = "post_id",
            ["eav-key"] = "meta_key",
            ["eav-value"] = "meta_value",
        });
        var tables = new IDbTable[] { metaTable };

        var configs = DbModel.CollectEavConfigs(tables);

        configs.Should().BeEmpty();
    }

    [Fact]
    public void CollectEavConfigs_ShortParentName_ResolvesByPrefix()
    {
        // Parent specified as "posts" but actual table is "wp_postmeta" -> prefix "wp_" -> "wp_posts"
        var metaTable = MakeTableWithMetadata("wp_postmeta", "dbo", new Dictionary<string, object?>
        {
            ["eav-parent"] = "posts",
            ["eav-fk"] = "post_id",
            ["eav-key"] = "meta_key",
            ["eav-value"] = "meta_value",
        });
        var parentTable = MakeTable("wp_posts");
        var tables = new IDbTable[] { parentTable, metaTable };

        var configs = DbModel.CollectEavConfigs(tables);

        configs.Should().ContainSingle();
        configs[0].ParentTableDbName.Should().Be("wp_posts");
    }

    [Fact]
    public void CollectEavConfigs_MultipleTables_CollectsAll()
    {
        var postmeta = MakeTableWithMetadata("wp_postmeta", "dbo", new Dictionary<string, object?>
        {
            ["eav-parent"] = "wp_posts",
            ["eav-fk"] = "post_id",
            ["eav-key"] = "meta_key",
            ["eav-value"] = "meta_value",
        });
        var usermeta = MakeTableWithMetadata("wp_usermeta", "dbo", new Dictionary<string, object?>
        {
            ["eav-parent"] = "wp_users",
            ["eav-fk"] = "user_id",
            ["eav-key"] = "meta_key",
            ["eav-value"] = "meta_value",
        });
        var posts = MakeTable("wp_posts");
        var users = MakeTable("wp_users");
        var tables = new IDbTable[] { posts, users, postmeta, usermeta };

        var configs = DbModel.CollectEavConfigs(tables);

        configs.Should().HaveCount(2);
        configs.Should().Contain(c => c.MetaTableDbName == "wp_postmeta" && c.ParentTableDbName == "wp_posts");
        configs.Should().Contain(c => c.MetaTableDbName == "wp_usermeta" && c.ParentTableDbName == "wp_users");
    }

    [Fact]
    public void CollectEavConfigs_NoEavMetadata_ReturnsEmpty()
    {
        var tables = new IDbTable[] { MakeTable("wp_posts"), MakeTable("wp_users") };

        var configs = DbModel.CollectEavConfigs(tables);

        configs.Should().BeEmpty();
    }

    #endregion

    #region WordPress Detector EAV Metadata Injection

    [Fact]
    public void WordPressDetector_InjectsEavMetadata_ForPostmeta()
    {
        var detector = new WordPressDetector();
        var tables = MakeTables(StandardWpTables);

        var result = detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var meta = result!.AdditionalMetadata;

        meta.Should().ContainKey("dbo.wp_postmeta");
        meta["dbo.wp_postmeta"]["eav-parent"].Should().Be("wp_posts");
        meta["dbo.wp_postmeta"]["eav-fk"].Should().Be("post_id");
        meta["dbo.wp_postmeta"]["eav-key"].Should().Be("meta_key");
        meta["dbo.wp_postmeta"]["eav-value"].Should().Be("meta_value");
    }

    [Fact]
    public void WordPressDetector_InjectsEavMetadata_ForUsermeta()
    {
        var detector = new WordPressDetector();
        var tables = MakeTables(StandardWpTables);

        var result = detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var meta = result!.AdditionalMetadata;

        meta.Should().ContainKey("dbo.wp_usermeta");
        meta["dbo.wp_usermeta"]["eav-parent"].Should().Be("wp_users");
        meta["dbo.wp_usermeta"]["eav-fk"].Should().Be("user_id");
        meta["dbo.wp_usermeta"]["eav-key"].Should().Be("meta_key");
        meta["dbo.wp_usermeta"]["eav-value"].Should().Be("meta_value");
    }

    [Fact]
    public void WordPressDetector_InjectsEavMetadata_ForTermmeta()
    {
        var detector = new WordPressDetector();
        var tables = MakeTables(StandardWpTables);

        var result = detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var meta = result!.AdditionalMetadata;

        meta.Should().ContainKey("dbo.wp_termmeta");
        meta["dbo.wp_termmeta"]["eav-parent"].Should().Be("wp_terms");
        meta["dbo.wp_termmeta"]["eav-fk"].Should().Be("term_id");
        meta["dbo.wp_termmeta"]["eav-key"].Should().Be("meta_key");
        meta["dbo.wp_termmeta"]["eav-value"].Should().Be("meta_value");
    }

    [Fact]
    public void WordPressDetector_InjectsEavMetadata_ForCommentmeta()
    {
        var detector = new WordPressDetector();
        var tables = MakeTables(StandardWpTables);

        var result = detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var meta = result!.AdditionalMetadata;

        meta.Should().ContainKey("dbo.wp_commentmeta");
        meta["dbo.wp_commentmeta"]["eav-parent"].Should().Be("wp_comments");
        meta["dbo.wp_commentmeta"]["eav-fk"].Should().Be("comment_id");
        meta["dbo.wp_commentmeta"]["eav-key"].Should().Be("meta_key");
        meta["dbo.wp_commentmeta"]["eav-value"].Should().Be("meta_value");
    }

    [Fact]
    public void WordPressDetector_EavMetadata_UsesFullPrefixedNames()
    {
        var detector = new WordPressDetector();
        var tableNames = new[]
        {
            "blog_users", "blog_usermeta", "blog_posts", "blog_postmeta",
            "blog_comments", "blog_commentmeta", "blog_options",
            "blog_terms", "blog_termmeta", "blog_term_taxonomy", "blog_term_relationships", "blog_links"
        };
        var tables = MakeTables(tableNames);

        var result = detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var meta = result!.AdditionalMetadata;

        meta["dbo.blog_postmeta"]["eav-parent"].Should().Be("blog_posts");
        meta["dbo.blog_usermeta"]["eav-parent"].Should().Be("blog_users");
        meta["dbo.blog_termmeta"]["eav-parent"].Should().Be("blog_terms");
        meta["dbo.blog_commentmeta"]["eav-parent"].Should().Be("blog_comments");
    }

    [Fact]
    public void WordPressDetector_EavMetadata_SkipsWhenMetaTableMissing()
    {
        var detector = new WordPressDetector();
        // Only posts, users, options — no postmeta or usermeta
        var tables = MakeTables("wp_users", "wp_posts", "wp_options");

        var result = detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.AdditionalMetadata.Keys.Should().NotContain(k => k.Contains("eav-parent"));
        // No table should have eav-parent in its metadata values
        foreach (var kvp in result.AdditionalMetadata)
        {
            kvp.Value.Should().NotContainKey("eav-parent");
        }
    }

    [Fact]
    public void WordPressDetector_EavMetadata_PreservesExistingLabelMetadata()
    {
        var detector = new WordPressDetector();
        var tables = MakeTables(StandardWpTables);

        var result = detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var meta = result!.AdditionalMetadata;

        // postmeta should have both label AND eav metadata
        meta["dbo.wp_postmeta"].Should().ContainKey("label");
        meta["dbo.wp_postmeta"]["label"].Should().Be("Post Meta");
        meta["dbo.wp_postmeta"].Should().ContainKey("eav-parent");
    }

    #endregion

    #region Schema Generation

    [Fact]
    public void SchemaGeneration_AddsMetaField_WhenTableIsEavParent()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("wp_posts", t => t
                .WithPrimaryKey("ID")
                .WithColumn("post_title", "nvarchar"))
            .WithTable("wp_postmeta", t => t
                .WithPrimaryKey("meta_id")
                .WithColumn("post_id", "int")
                .WithColumn("meta_key", "nvarchar")
                .WithColumn("meta_value", "nvarchar"))
            .WithEavConfig("wp_postmeta", "wp_posts", "post_id", "meta_key", "meta_value")
            .Build();

        var generator = new TableSchemaGenerator(
            model.Tables.First(t => t.DbName == "wp_posts"));

        var sdl = generator.GetTableTypeDefinition(model, includeDynamicJoins: false);

        sdl.Should().Contain("_meta: String");
    }

    [Fact]
    public void SchemaGeneration_NoMetaField_WhenNoEavConfig()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("wp_posts", t => t
                .WithPrimaryKey("ID")
                .WithColumn("post_title", "nvarchar"))
            .Build();

        var generator = new TableSchemaGenerator(
            model.Tables.First(t => t.DbName == "wp_posts"));

        var sdl = generator.GetTableTypeDefinition(model, includeDynamicJoins: false);

        sdl.Should().NotContain("_meta");
    }

    [Fact]
    public void SchemaGeneration_MetaFieldOnlyOnParent_NotOnMetaTable()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("wp_posts", t => t
                .WithPrimaryKey("ID")
                .WithColumn("post_title", "nvarchar"))
            .WithTable("wp_postmeta", t => t
                .WithPrimaryKey("meta_id")
                .WithColumn("post_id", "int")
                .WithColumn("meta_key", "nvarchar")
                .WithColumn("meta_value", "nvarchar"))
            .WithEavConfig("wp_postmeta", "wp_posts", "post_id", "meta_key", "meta_value")
            .Build();

        var metaGenerator = new TableSchemaGenerator(
            model.Tables.First(t => t.DbName == "wp_postmeta"));

        var sdl = metaGenerator.GetTableTypeDefinition(model, includeDynamicJoins: false);

        sdl.Should().NotContain("_meta: String");
    }

    [Fact]
    public void SchemaGeneration_MultipleEavParents_EachGetsMetaField()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("wp_posts", t => t
                .WithPrimaryKey("ID")
                .WithColumn("post_title", "nvarchar"))
            .WithTable("wp_users", t => t
                .WithPrimaryKey("ID")
                .WithColumn("user_login", "nvarchar"))
            .WithTable("wp_postmeta", t => t
                .WithPrimaryKey("meta_id")
                .WithColumn("post_id", "int")
                .WithColumn("meta_key", "nvarchar")
                .WithColumn("meta_value", "nvarchar"))
            .WithTable("wp_usermeta", t => t
                .WithPrimaryKey("umeta_id")
                .WithColumn("user_id", "int")
                .WithColumn("meta_key", "nvarchar")
                .WithColumn("meta_value", "nvarchar"))
            .WithEavConfig("wp_postmeta", "wp_posts", "post_id", "meta_key", "meta_value")
            .WithEavConfig("wp_usermeta", "wp_users", "user_id", "meta_key", "meta_value")
            .Build();

        var postsGenerator = new TableSchemaGenerator(
            model.Tables.First(t => t.DbName == "wp_posts"));
        var usersGenerator = new TableSchemaGenerator(
            model.Tables.First(t => t.DbName == "wp_users"));

        postsGenerator.GetTableTypeDefinition(model, false).Should().Contain("_meta: String");
        usersGenerator.GetTableTypeDefinition(model, false).Should().Contain("_meta: String");
    }

    #endregion

    #region IDbModel Interface

    [Fact]
    public void IDbModel_EavConfigs_DefaultsToEmpty()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id"))
            .Build();

        model.EavConfigs.Should().BeEmpty();
    }

    #endregion
}
