using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.Storage;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model.AppSchema;

public class WordPressSchemaBundleTests
{
    #region Helper Methods

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

    private static readonly string[] StandardWpTables =
    {
        "wp_users", "wp_usermeta", "wp_posts", "wp_postmeta",
        "wp_comments", "wp_commentmeta", "wp_options",
        "wp_terms", "wp_termmeta", "wp_term_taxonomy", "wp_term_relationships", "wp_links"
    };

    #endregion

    #region Constructor and Configuration

    [Fact]
    public void Constructor_DefaultConfig_UsesDefaultConfiguration()
    {
        var bundle = new WordPressSchemaBundle();

        bundle.Configuration.Should().NotBeNull();
        bundle.Configuration.EnableAutoDetection.Should().BeTrue();
        bundle.Configuration.EnablePhpSerialization.Should().BeTrue();
        bundle.Configuration.EnableEavFlattening.Should().BeTrue();
        bundle.Configuration.EnableFileStorage.Should().BeFalse();
    }

    [Fact]
    public void Constructor_CustomConfig_UsesProvidedConfiguration()
    {
        var config = new WordPressBundleConfiguration
        {
            EnableAutoDetection = false,
            EnablePhpSerialization = false,
        };
        var bundle = new WordPressSchemaBundle(config);

        bundle.Configuration.EnableAutoDetection.Should().BeFalse();
        bundle.Configuration.EnablePhpSerialization.Should().BeFalse();
    }

    [Fact]
    public void Detector_Property_ReturnsWordPressDetector()
    {
        var bundle = new WordPressSchemaBundle();

        bundle.Detector.Should().NotBeNull();
        bundle.Detector.AppName.Should().Be("wordpress");
    }

    #endregion

    #region Detection

    [Fact]
    public void Detect_WordPressTables_ReturnsDetectionResult()
    {
        var bundle = new WordPressSchemaBundle();
        var tables = MakeTables(StandardWpTables);

        var result = bundle.Detect(tables);

        result.Should().NotBeNull();
        result!.AppName.Should().Be("wordpress");
        result.Confidence.Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void Detect_NonWordPressTables_ReturnsNull()
    {
        var bundle = new WordPressSchemaBundle();
        var tables = MakeTables("customers", "orders", "products");

        var result = bundle.Detect(tables);

        result.Should().BeNull();
    }

    [Fact]
    public void Detect_AutoDetectionDisabled_ReturnsNull()
    {
        var config = new WordPressBundleConfiguration { EnableAutoDetection = false };
        var bundle = new WordPressSchemaBundle(config);
        var tables = MakeTables(StandardWpTables);

        var result = bundle.Detect(tables);

        result.Should().BeNull();
    }

    [Fact]
    public void IsEnabled_DefaultMetadata_ReturnsTrue()
    {
        var bundle = new WordPressSchemaBundle();
        var metadata = new Dictionary<string, object?>();

        var result = bundle.IsEnabled(metadata);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_DisabledInMetadata_ReturnsFalse()
    {
        var bundle = new WordPressSchemaBundle();
        var metadata = new Dictionary<string, object?> { ["auto-detect-app"] = "disabled" };

        var result = bundle.IsEnabled(metadata);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_AutoDetectionDisabled_ReturnsFalse()
    {
        var config = new WordPressBundleConfiguration { EnableAutoDetection = false };
        var bundle = new WordPressSchemaBundle(config);
        var metadata = new Dictionary<string, object?>();

        var result = bundle.IsEnabled(metadata);

        result.Should().BeFalse();
    }

    #endregion

    #region Metadata Annotations

    [Fact]
    public void BuildMetadataAnnotations_DetectionResult_ReturnsAnnotations()
    {
        var bundle = new WordPressSchemaBundle();
        var tables = MakeTables(StandardWpTables);
        var detectionResult = bundle.Detect(tables)!;

        var annotations = bundle.BuildMetadataAnnotations(detectionResult);

        annotations.Should().NotBeEmpty();
        // Should include labels for core tables
        annotations.Should().Contain(a => a.Contains("wp_posts") && a.Contains("label:"));
        annotations.Should().Contain(a => a.Contains("wp_users") && a.Contains("label:"));
    }

    [Fact]
    public void BuildMetadataAnnotations_IncludesEavMetadata()
    {
        var bundle = new WordPressSchemaBundle();
        var tables = MakeTables(StandardWpTables);
        var detectionResult = bundle.Detect(tables)!;

        var annotations = bundle.BuildMetadataAnnotations(detectionResult);

        // Should include EAV configuration for postmeta
        annotations.Should().Contain(a => a.Contains("wp_postmeta") && a.Contains("eav-parent:"));
    }

    #endregion

    #region EAV Configs

    [Fact]
    public void BuildEavConfigs_EavEnabled_ReturnsConfigs()
    {
        var bundle = new WordPressSchemaBundle();
        var tables = MakeTables(StandardWpTables);
        var detectionResult = bundle.Detect(tables)!;

        var configs = bundle.BuildEavConfigs(detectionResult);

        configs.Should().NotBeEmpty();
        configs.Should().Contain(c => c.MetaTableDbName == "wp_postmeta" && c.ParentTableDbName == "wp_posts");
        configs.Should().Contain(c => c.MetaTableDbName == "wp_usermeta" && c.ParentTableDbName == "wp_users");
        configs.Should().Contain(c => c.MetaTableDbName == "wp_termmeta" && c.ParentTableDbName == "wp_terms");
        configs.Should().Contain(c => c.MetaTableDbName == "wp_commentmeta" && c.ParentTableDbName == "wp_comments");
    }

    [Fact]
    public void BuildEavConfigs_EavDisabled_ReturnsEmpty()
    {
        var config = new WordPressBundleConfiguration { EnableEavFlattening = false };
        var bundle = new WordPressSchemaBundle(config);
        var tables = MakeTables(StandardWpTables);
        var detectionResult = bundle.Detect(tables)!;

        var configs = bundle.BuildEavConfigs(detectionResult);

        configs.Should().BeEmpty();
    }

    [Fact]
    public void BuildEavConfigs_ConfigsHaveCorrectStructure()
    {
        var bundle = new WordPressSchemaBundle();
        var tables = MakeTables(StandardWpTables);
        var detectionResult = bundle.Detect(tables)!;

        var configs = bundle.BuildEavConfigs(detectionResult);
        var postmetaConfig = configs.First(c => c.MetaTableDbName == "wp_postmeta");

        postmetaConfig.ForeignKeyColumn.Should().Be("post_id");
        postmetaConfig.KeyColumn.Should().Be("meta_key");
        postmetaConfig.ValueColumn.Should().Be("meta_value");
    }

    #endregion

    #region File Storage

    [Fact]
    public void BuildFileStorageConfig_FileStorageEnabledWithConfig_ReturnsConfig()
    {
        var storageConfig = new StorageBucketConfig
        {
            BucketName = "/uploads",
            ProviderType = "local"
        };
        var config = new WordPressBundleConfiguration
        {
            EnableFileStorage = true,
            FileStorageConfig = storageConfig
        };
        var bundle = new WordPressSchemaBundle(config);
        var tables = MakeTables(StandardWpTables);
        var detectionResult = bundle.Detect(tables)!;

        var fileConfig = bundle.BuildFileStorageConfig(detectionResult);

        fileConfig.Should().NotBeNull();
        fileConfig!.BucketConfig.Should().Be(storageConfig);
        fileConfig.AttachmentTablePattern.Should().Be("_posts");
        fileConfig.AttachmentTypeColumn.Should().Be("post_type");
        fileConfig.AttachmentTypeValue.Should().Be("attachment");
    }

    [Fact]
    public void BuildFileStorageConfig_FileStorageDisabled_ReturnsNull()
    {
        var bundle = new WordPressSchemaBundle();
        var tables = MakeTables(StandardWpTables);
        var detectionResult = bundle.Detect(tables)!;

        var fileConfig = bundle.BuildFileStorageConfig(detectionResult);

        fileConfig.Should().BeNull();
    }

    [Fact]
    public void BuildFileStorageConfig_NoStorageConfig_ReturnsNull()
    {
        var config = new WordPressBundleConfiguration { EnableFileStorage = true };
        var bundle = new WordPressSchemaBundle(config);
        var tables = MakeTables(StandardWpTables);
        var detectionResult = bundle.Detect(tables)!;

        var fileConfig = bundle.BuildFileStorageConfig(detectionResult);

        fileConfig.Should().BeNull();
    }

    #endregion

    #region Serialized Column Metadata

    [Fact]
    public void GetSerializedColumnMetadata_PhpEnabled_ReturnsMetadata()
    {
        var bundle = new WordPressSchemaBundle();
        var tables = new List<IDbTable>
        {
            MakeTableWithColumns("wp_posts", "dbo", ("ID", "int"), ("post_title", "nvarchar")),
            MakeTableWithColumns("wp_postmeta", "dbo", ("meta_id", "int"), ("post_id", "int"), ("meta_key", "nvarchar"), ("meta_value", "longtext")),
            MakeTableWithColumns("wp_users", "dbo", ("ID", "int")),
            MakeTableWithColumns("wp_options", "dbo", ("option_id", "int"), ("option_name", "nvarchar"), ("option_value", "longtext")),
        };
        var detectionResult = bundle.Detect(tables)!;

        var metadata = bundle.GetSerializedColumnMetadata(detectionResult);

        metadata.Should().NotBeEmpty();
        metadata.Should().ContainKey("dbo.wp_postmeta.meta_value");
        metadata.Should().ContainKey("dbo.wp_options.option_value");
    }

    [Fact]
    public void GetSerializedColumnMetadata_PhpDisabled_ReturnsEmpty()
    {
        var config = new WordPressBundleConfiguration { EnablePhpSerialization = false };
        var bundle = new WordPressSchemaBundle(config);
        var tables = new List<IDbTable>
        {
            MakeTableWithColumns("wp_posts", "dbo", ("ID", "int")),
            MakeTableWithColumns("wp_postmeta", "dbo", ("meta_id", "int"), ("post_id", "int"), ("meta_key", "nvarchar"), ("meta_value", "longtext")),
            MakeTableWithColumns("wp_users", "dbo", ("ID", "int")),
            MakeTableWithColumns("wp_options", "dbo", ("option_id", "int"), ("option_name", "nvarchar"), ("option_value", "longtext")),
        };
        var detectionResult = bundle.Detect(tables)!;

        var metadata = bundle.GetSerializedColumnMetadata(detectionResult);

        metadata.Should().BeEmpty();
    }

    #endregion

    #region Foreign Keys

    [Fact]
    public void GetForeignKeys_FkEnabled_ReturnsForeignKeys()
    {
        var bundle = new WordPressSchemaBundle();
        var tables = MakeTables(StandardWpTables);
        var detectionResult = bundle.Detect(tables)!;

        var fks = bundle.GetForeignKeys(detectionResult);

        fks.Should().NotBeEmpty();
        fks.Should().Contain(fk => fk.ChildTable == "wp_posts" && fk.ChildColumn == "post_author" && fk.ParentTable == "wp_users");
        fks.Should().Contain(fk => fk.ChildTable == "wp_postmeta" && fk.ChildColumn == "post_id" && fk.ParentTable == "wp_posts");
    }

    [Fact]
    public void GetForeignKeys_FkDisabled_ReturnsEmpty()
    {
        var config = new WordPressBundleConfiguration { InjectForeignKeys = false };
        var bundle = new WordPressSchemaBundle(config);
        var tables = MakeTables(StandardWpTables);
        var detectionResult = bundle.Detect(tables)!;

        var fks = bundle.GetForeignKeys(detectionResult);

        fks.Should().BeEmpty();
    }

    #endregion

    #region Prefix Detection

    [Fact]
    public void GetDetectedPrefixes_StandardWordPress_ReturnsWpPrefix()
    {
        var bundle = new WordPressSchemaBundle();
        var tables = MakeTables(StandardWpTables);
        var detectionResult = bundle.Detect(tables)!;

        var prefixes = bundle.GetDetectedPrefixes(detectionResult);

        prefixes.Should().ContainSingle();
        prefixes.Should().Contain("wp_");
    }

    [Fact]
    public void GetDetectedPrefixes_Multisite_ReturnsAllPrefixes()
    {
        var bundle = new WordPressSchemaBundle();
        var tableNames = StandardWpTables.Concat(new[]
        {
            "wp2_users", "wp2_posts", "wp2_options", "wp2_postmeta"
        }).ToArray();
        var tables = MakeTables(tableNames);
        var detectionResult = bundle.Detect(tables)!;

        var prefixes = bundle.GetDetectedPrefixes(detectionResult);

        prefixes.Should().HaveCount(2);
        prefixes.Should().Contain("wp_");
        prefixes.Should().Contain("wp2_");
    }

    #endregion

    #region Configuration Presets

    [Fact]
    public void DefaultConfiguration_HasExpectedValues()
    {
        var config = WordPressBundleConfiguration.Default;

        config.EnableAutoDetection.Should().BeTrue();
        config.EnablePhpSerialization.Should().BeTrue();
        config.EnableEavFlattening.Should().BeTrue();
        config.EnableFileStorage.Should().BeFalse();
        config.HideActionSchedulerTables.Should().BeTrue();
        config.InjectTableLabels.Should().BeTrue();
        config.InjectForeignKeys.Should().BeTrue();
    }

    [Fact]
    public void MinimalConfiguration_HasExpectedValues()
    {
        var config = WordPressBundleConfiguration.Minimal;

        config.EnableAutoDetection.Should().BeTrue();
        config.EnablePhpSerialization.Should().BeFalse();
        config.EnableEavFlattening.Should().BeFalse();
        config.EnableFileStorage.Should().BeFalse();
        config.HideActionSchedulerTables.Should().BeTrue();
        config.InjectTableLabels.Should().BeFalse();
        config.InjectForeignKeys.Should().BeTrue();
    }

    [Fact]
    public void FullFeaturedConfiguration_HasExpectedValues()
    {
        var storageConfig = new StorageBucketConfig { BucketName = "test", ProviderType = "local" };
        var config = WordPressBundleConfiguration.FullFeatured(storageConfig);

        config.EnableAutoDetection.Should().BeTrue();
        config.EnablePhpSerialization.Should().BeTrue();
        config.EnableEavFlattening.Should().BeTrue();
        config.EnableFileStorage.Should().BeTrue();
        config.FileStorageConfig.Should().Be(storageConfig);
    }

    #endregion
}
