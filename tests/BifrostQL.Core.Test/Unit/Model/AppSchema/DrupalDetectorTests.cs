using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model.AppSchema;

public class DrupalDetectorTests
{
    private readonly DrupalDetector _detector = new();

    #region Helper

    private static IReadOnlyList<IDbTable> MakeTables(params string[] tableNames)
        => tableNames.Select(name => MakeTable(name, "public")).ToList();

    private static IDbTable MakeTable(string dbName, string schema = "public")
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

    private static readonly string[] SignatureTables =
    {
        "node", "node_field_data", "users_field_data"
    };

    private static readonly string[] CommonSupportingTables =
    {
        "node_access", "node_revision", "taxonomy_term_field_data",
        "taxonomy_term_hierarchy", "users", "sessions"
    };

    #endregion

    #region Detection

    [Fact]
    public void Detect_AllSignatureTablesPresent_DetectsDrupal()
    {
        var tables = MakeTables(SignatureTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.AppName.Should().Be("drupal");
        result.Confidence.Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void Detect_MissingSignatureTable_ReturnsNull()
    {
        // Missing users_field_data
        var tables = MakeTables("node", "node_field_data");

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().BeNull();
    }

    [Fact]
    public void Detect_EmptyTables_ReturnsNull()
    {
        var result = _detector.Detect(Array.Empty<IDbTable>(), Array.Empty<string>());
        result.Should().BeNull();
    }

    [Fact]
    public void Detect_ConflictingSchemaName_BailsOut()
    {
        var tables = MakeTables(SignatureTables);

        var result = _detector.Detect(tables, new[] { "drupal" });

        result.Should().BeNull();
    }

    #endregion

    #region Confidence Scoring

    [Fact]
    public void Detect_OnlySignatureTables_HasBaseConfidence()
    {
        var tables = MakeTables(SignatureTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        // Should have base confidence of ~0.65
        result!.Confidence.Should().BeGreaterThanOrEqualTo(0.65);
        result.Confidence.Should().BeLessThan(0.7);
    }

    [Fact]
    public void Detect_WithSupportingTables_HasHigherConfidence()
    {
        var tables = MakeTables(SignatureTables.Concat(CommonSupportingTables).ToArray());

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.Confidence.Should().BeGreaterThan(0.7);
    }

    [Fact]
    public void Detect_ConfidenceClampedToMaximum()
    {
        // Add all possible supporting tables
        var allTables = SignatureTables.Concat(new[]
        {
            "node_access", "node_revision", "node_field_revision",
            "taxonomy_term_field_data", "taxonomy_term_hierarchy", "taxonomy_index",
            "comment_field_data", "comment_entity_statistics",
            "users", "sessions", "key_value", "config",
            "cache_bootstrap", "cache_config", "cache_data", "cache_default",
            "cache_discovery", "cache_entity", "cache_menu", "cache_page",
            "cache_render", "cache_toolbar", "watchdog", "semaphore", "queue"
        }).ToArray();
        var tables = MakeTables(allTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.Confidence.Should().BeLessThanOrEqualTo(1.0);
    }

    #endregion

    #region Prefix Groups

    [Fact]
    public void Detect_CreatesSinglePrefixGroup()
    {
        var tables = MakeTables(SignatureTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        result!.SchemaResult.PrefixGroups.Should().ContainSingle();
        result.SchemaResult.PrefixGroups[0].Prefix.Should().Be("");
        result.SchemaResult.PrefixGroups[0].GroupName.Should().Be("drupal");
    }

    [Fact]
    public void Detect_PrefixGroupContainsAllTables()
    {
        var tableNames = SignatureTables.Concat(CommonSupportingTables).ToArray();
        var tables = MakeTables(tableNames);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var group = result!.SchemaResult.PrefixGroups[0];
        foreach (var name in tableNames)
            group.TableDbNames.Should().Contain(name);
    }

    #endregion

    #region Metadata: Hidden Tables

    [Fact]
    public void Detect_HidesCacheTables()
    {
        var tables = MakeTables(SignatureTables.Concat(new[] { "cache_bootstrap", "cache_config", "cache_data" }).ToArray());

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var meta = result!.SchemaResult.AdditionalMetadata;

        meta.Should().ContainKey("public.cache_bootstrap");
        meta["public.cache_bootstrap"]["visibility"].Should().Be("hidden");
        meta.Should().ContainKey("public.cache_config");
        meta["public.cache_config"]["visibility"].Should().Be("hidden");
    }

    [Fact]
    public void Detect_HidesSystemTables()
    {
        var tables = MakeTables(SignatureTables.Concat(new[] { "semaphore", "queue", "sessions", "watchdog" }).ToArray());

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var meta = result!.SchemaResult.AdditionalMetadata;

        meta.Should().ContainKey("public.semaphore");
        meta["public.semaphore"]["visibility"].Should().Be("hidden");
        meta.Should().ContainKey("public.sessions");
        meta["public.sessions"]["visibility"].Should().Be("hidden");
    }

    #endregion

    #region Metadata: Labels

    [Fact]
    public void Detect_InjectsLabels()
    {
        var tables = MakeTables(SignatureTables.Concat(new[] { "node_access", "users" }).ToArray());

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var meta = result!.SchemaResult.AdditionalMetadata;

        meta.Should().ContainKey("public.node");
        meta["public.node"]["label"].Should().Be("Nodes");

        meta.Should().ContainKey("public.node_field_data");
        meta["public.node_field_data"]["label"].Should().Be("Node Data");

        meta.Should().ContainKey("public.users_field_data");
        meta["public.users_field_data"]["label"].Should().Be("User Data");

        meta.Should().ContainKey("public.users");
        meta["public.users"]["label"].Should().Be("Users");
    }

    #endregion

    #region Explicit Foreign Keys

    [Fact]
    public void Detect_GeneratesExplicitFKs()
    {
        var tables = MakeTables(SignatureTables.Concat(new[]
        {
            "node_revision", "node_field_revision", "taxonomy_term_field_data",
            "taxonomy_term_hierarchy", "taxonomy_index", "comment_field_data"
        }).ToArray());

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        var fks = result!.SchemaResult.ExplicitForeignKeys;

        fks.Should().Contain(fk => fk.ChildTable == "node_field_data" && fk.ChildColumn == "uid" && fk.ParentTable == "users_field_data");
        fks.Should().Contain(fk => fk.ChildTable == "node_field_data" && fk.ChildColumn == "vid" && fk.ParentTable == "node_revision");
        fks.Should().Contain(fk => fk.ChildTable == "node_revision" && fk.ChildColumn == "nid" && fk.ParentTable == "node");
    }

    [Fact]
    public void Detect_FKsOnlyForExistingTables()
    {
        // Only signature tables - no revision tables
        var tables = MakeTables(SignatureTables);

        var result = _detector.Detect(tables, Array.Empty<string>());

        result.Should().NotBeNull();
        // FKs requiring revision tables should NOT be present
        result!.SchemaResult.ExplicitForeignKeys.Should().NotContain(fk => fk.ChildTable == "node_revision");
        result.SchemaResult.ExplicitForeignKeys.Should().NotContain(fk => fk.ParentTable == "node_revision");
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

    #endregion
}
