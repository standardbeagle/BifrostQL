using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Server;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

public class MetadataSourceTests
{
    #region CompositeMetadataSource

    [Fact]
    public async Task Composite_EmptySources_ReturnsEmptyDictionary()
    {
        var composite = new CompositeMetadataSource(Array.Empty<IMetadataSource>());
        var result = await composite.LoadTableMetadataAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Composite_SingleSource_ReturnsSameData()
    {
        var source = new InMemoryMetadataSource(10, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.Users"] = new Dictionary<string, object?> { ["tenant-filter"] = "tenant_id" }
        });

        var composite = new CompositeMetadataSource(new[] { source });
        var result = await composite.LoadTableMetadataAsync();

        result.Should().ContainKey("dbo.Users");
        result["dbo.Users"]["tenant-filter"].Should().Be("tenant_id");
    }

    [Fact]
    public async Task Composite_HigherPriorityOverridesLower()
    {
        var low = new InMemoryMetadataSource(0, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.Users"] = new Dictionary<string, object?>
            {
                ["tenant-filter"] = "old_tenant_id",
                ["label"] = "Name"
            }
        });

        var high = new InMemoryMetadataSource(100, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.Users"] = new Dictionary<string, object?> { ["tenant-filter"] = "new_tenant_id" }
        });

        var composite = new CompositeMetadataSource(new IMetadataSource[] { high, low });
        var result = await composite.LoadTableMetadataAsync();

        result["dbo.Users"]["tenant-filter"].Should().Be("new_tenant_id");
        result["dbo.Users"]["label"].Should().Be("Name");
    }

    [Fact]
    public async Task Composite_MergesTablesFromDifferentSources()
    {
        var source1 = new InMemoryMetadataSource(0, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.Users"] = new Dictionary<string, object?> { ["tenant-filter"] = "tenant_id" }
        });

        var source2 = new InMemoryMetadataSource(100, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.Orders"] = new Dictionary<string, object?> { ["soft-delete"] = "deleted_at" }
        });

        var composite = new CompositeMetadataSource(new IMetadataSource[] { source1, source2 });
        var result = await composite.LoadTableMetadataAsync();

        result.Should().ContainKey("dbo.Users");
        result.Should().ContainKey("dbo.Orders");
    }

    [Fact]
    public async Task Composite_Priority_ReturnsMaxOfSourcePriorities()
    {
        var sources = new IMetadataSource[]
        {
            new InMemoryMetadataSource(10, new Dictionary<string, IDictionary<string, object?>>()),
            new InMemoryMetadataSource(50, new Dictionary<string, IDictionary<string, object?>>()),
        };

        var composite = new CompositeMetadataSource(sources);
        composite.Priority.Should().Be(50);
    }

    [Fact]
    public async Task Composite_Priority_EmptySources_ReturnsZero()
    {
        var composite = new CompositeMetadataSource(Array.Empty<IMetadataSource>());
        composite.Priority.Should().Be(0);
    }

    [Fact]
    public async Task Composite_RootMetadata_MergedCorrectly()
    {
        var low = new InMemoryMetadataSource(0, new Dictionary<string, IDictionary<string, object?>>
        {
            [":root"] = new Dictionary<string, object?>
            {
                ["auto-join"] = "true",
                ["default-limit"] = "100"
            }
        });

        var high = new InMemoryMetadataSource(100, new Dictionary<string, IDictionary<string, object?>>
        {
            [":root"] = new Dictionary<string, object?> { ["default-limit"] = "50" }
        });

        var composite = new CompositeMetadataSource(new IMetadataSource[] { low, high });
        var result = await composite.LoadTableMetadataAsync();

        result[":root"]["auto-join"].Should().Be("true");
        result[":root"]["default-limit"].Should().Be("50");
    }

    #endregion

    #region Merge Behavior (via Composite)

    [Fact]
    public async Task Merge_NewTable_AddedFromSource()
    {
        var source = new InMemoryMetadataSource(0, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.Users"] = new Dictionary<string, object?> { ["soft-delete"] = "deleted_at" }
        });

        var composite = new CompositeMetadataSource(new[] { source });
        var result = await composite.LoadTableMetadataAsync();

        result.Should().ContainKey("dbo.Users");
        result["dbo.Users"]["soft-delete"].Should().Be("deleted_at");
    }

    [Fact]
    public async Task Merge_ExistingTable_HigherPriorityOverridesKeys()
    {
        var low = new InMemoryMetadataSource(0, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.Users"] = new Dictionary<string, object?> { ["soft-delete"] = "old" }
        });
        var high = new InMemoryMetadataSource(100, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.Users"] = new Dictionary<string, object?> { ["soft-delete"] = "new" }
        });

        var composite = new CompositeMetadataSource(new IMetadataSource[] { low, high });
        var result = await composite.LoadTableMetadataAsync();

        result["dbo.Users"]["soft-delete"].Should().Be("new");
    }

    [Fact]
    public async Task Merge_ExistingTable_PreservesNonOverriddenKeys()
    {
        var low = new InMemoryMetadataSource(0, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.Users"] = new Dictionary<string, object?>
            {
                ["soft-delete"] = "deleted_at",
                ["label"] = "Name"
            }
        });
        var high = new InMemoryMetadataSource(100, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.Users"] = new Dictionary<string, object?> { ["soft-delete"] = "new" }
        });

        var composite = new CompositeMetadataSource(new IMetadataSource[] { low, high });
        var result = await composite.LoadTableMetadataAsync();

        result["dbo.Users"]["label"].Should().Be("Name");
    }

    #endregion

    #region FileMetadataSource

    [Fact]
    public async Task FileSource_ParsesSimpleRule()
    {
        var source = new FileMetadataSource(new[] { "dbo.users { tenant-filter: tenant_id }" });
        var result = await source.LoadTableMetadataAsync();

        result.Should().ContainKey("dbo.users");
        result["dbo.users"]["tenant-filter"].Should().Be("tenant_id");
    }

    [Fact]
    public async Task FileSource_ParsesMultipleProperties()
    {
        var source = new FileMetadataSource(new[] { "dbo.users { tenant-filter: tenant_id; soft-delete: deleted_at }" });
        var result = await source.LoadTableMetadataAsync();

        result["dbo.users"]["tenant-filter"].Should().Be("tenant_id");
        result["dbo.users"]["soft-delete"].Should().Be("deleted_at");
    }

    [Fact]
    public async Task FileSource_ParsesMultipleRules()
    {
        var source = new FileMetadataSource(new[]
        {
            "dbo.users { tenant-filter: tenant_id }",
            "dbo.orders { soft-delete: deleted_at }"
        });
        var result = await source.LoadTableMetadataAsync();

        result.Should().ContainKey("dbo.users");
        result.Should().ContainKey("dbo.orders");
    }

    [Fact]
    public async Task FileSource_ParsesRootMetadata()
    {
        var source = new FileMetadataSource(new[] { ":root { auto-join: true; default-limit: 100 }" });
        var result = await source.LoadTableMetadataAsync();

        result.Should().ContainKey(":root");
        result[":root"]["auto-join"].Should().Be("true");
        result[":root"]["default-limit"].Should().Be("100");
    }

    [Fact]
    public async Task FileSource_MergesRulesForSameTable()
    {
        var source = new FileMetadataSource(new[]
        {
            "dbo.users { tenant-filter: tenant_id }",
            "dbo.users { soft-delete: deleted_at }"
        });
        var result = await source.LoadTableMetadataAsync();

        result["dbo.users"]["tenant-filter"].Should().Be("tenant_id");
        result["dbo.users"]["soft-delete"].Should().Be("deleted_at");
    }

    [Fact]
    public async Task FileSource_LaterRuleOverridesEarlier()
    {
        var source = new FileMetadataSource(new[]
        {
            "dbo.users { tenant-filter: old_id }",
            "dbo.users { tenant-filter: new_id }"
        });
        var result = await source.LoadTableMetadataAsync();

        result["dbo.users"]["tenant-filter"].Should().Be("new_id");
    }

    [Fact]
    public async Task FileSource_IgnoresInvalidRules()
    {
        var source = new FileMetadataSource(new[]
        {
            "no braces here",
            "",
            "dbo.users { tenant-filter: tenant_id }"
        });
        var result = await source.LoadTableMetadataAsync();

        result.Should().HaveCount(1);
        result.Should().ContainKey("dbo.users");
    }

    [Fact]
    public async Task FileSource_Priority_IsZero()
    {
        var source = new FileMetadataSource(Array.Empty<string>());
        source.Priority.Should().Be(0);
    }

    [Fact]
    public async Task FileSource_EmptyRules_ReturnsEmpty()
    {
        var source = new FileMetadataSource(Array.Empty<string>());
        var result = await source.LoadTableMetadataAsync();
        result.Should().BeEmpty();
    }

    #endregion

    #region MetadataValidator

    [Fact]
    public void ValidateTableMetadata_KnownKeys_NoWarnings()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["tenant-filter"] = "tenant_id",
            ["soft-delete"] = "deleted_at",
            ["visibility"] = "hidden"
        };

        var warnings = MetadataValidator.ValidateTableMetadata("dbo.Users", metadata);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void ValidateTableMetadata_UnknownKey_ReturnsWarning()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["tenant-filter"] = "tenant_id",
            ["unknown-key"] = "value"
        };

        var warnings = MetadataValidator.ValidateTableMetadata("dbo.Users", metadata);
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("unknown-key");
        warnings[0].Should().Contain("dbo.Users");
    }

    [Fact]
    public void ValidateColumnMetadata_KnownKeys_NoWarnings()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["populate"] = "created-on",
            ["type"] = "json"
        };

        var warnings = MetadataValidator.ValidateColumnMetadata("dbo.Users", "CreatedAt", metadata);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void ValidateColumnMetadata_UnknownKey_ReturnsWarning()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["populate"] = "created-on",
            ["fake-key"] = "value"
        };

        var warnings = MetadataValidator.ValidateColumnMetadata("dbo.Users", "CreatedAt", metadata);
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("fake-key");
        warnings[0].Should().Contain("dbo.Users.CreatedAt");
    }

    [Fact]
    public void ValidateDatabaseMetadata_KnownKeys_NoWarnings()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["auto-join"] = "true",
            ["default-limit"] = "100",
            ["schema-prefix"] = "enabled"
        };

        var warnings = MetadataValidator.ValidateDatabaseMetadata(metadata);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void ValidateDatabaseMetadata_UnknownKey_ReturnsWarning()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["auto-join"] = "true",
            ["not-a-real-key"] = "value"
        };

        var warnings = MetadataValidator.ValidateDatabaseMetadata(metadata);
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("not-a-real-key");
    }

    [Fact]
    public void ValidateTableMetadata_EmptyMetadata_NoWarnings()
    {
        var warnings = MetadataValidator.ValidateTableMetadata("dbo.Users", new Dictionary<string, object?>());
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void ValidateColumnMetadata_EmptyMetadata_NoWarnings()
    {
        var warnings = MetadataValidator.ValidateColumnMetadata("dbo.Users", "Id", new Dictionary<string, object?>());
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void ValidateDatabaseMetadata_EmptyMetadata_NoWarnings()
    {
        var warnings = MetadataValidator.ValidateDatabaseMetadata(new Dictionary<string, object?>());
        warnings.Should().BeEmpty();
    }

    #endregion

    #region DbModel Integration

    [Fact]
    public void DbModel_AdditionalMetadata_AppliedToTable()
    {
        var additionalMetadata = new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase)
        {
            [".Orders"] = new Dictionary<string, object?> { ["soft-delete"] = "deleted_at" }
        };

        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal"))
            .Build();

        // The table's schema is empty string in test fixture, so key would be ".Orders"
        model.Tables.First().Metadata.Should().NotContainKey("soft-delete");
    }

    [Fact]
    public void DbModel_WithForeignKeys_AdditionalMetadata_AppliedCorrectly()
    {
        var additionalMetadata = new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.Orders"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["soft-delete"] = "deleted_at"
            }
        };

        var tables = new List<DbTable>
        {
            CreateTestTable("Orders", "dbo")
        };

        var metadataLoader = new TestNoOpMetadataLoader();
        var model = DbModel.FromTables(tables, metadataLoader, Array.Empty<DbStoredProcedure>(), Array.Empty<DbForeignKey>(), additionalMetadata);

        var orders = model.GetTableFromDbName("Orders");
        orders.GetMetadataValue("soft-delete").Should().Be("deleted_at");
    }

    [Fact]
    public void DbModel_AdditionalRootMetadata_MergedIntoDatabaseMetadata()
    {
        var additionalMetadata = new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase)
        {
            [":root"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["default-limit"] = "50"
            }
        };

        var tables = new List<DbTable>
        {
            CreateTestTable("Users", "dbo")
        };

        var metadataLoader = new TestNoOpMetadataLoader();
        var model = DbModel.FromTables(tables, metadataLoader, Array.Empty<DbStoredProcedure>(), Array.Empty<DbForeignKey>(), additionalMetadata);

        model.GetMetadataValue("default-limit").Should().Be("50");
    }

    [Fact]
    public void DbModel_AdditionalMetadata_OverridesMetadataLoaderValues()
    {
        var additionalMetadata = new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.Orders"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["soft-delete"] = "db_override"
            }
        };

        var tables = new List<DbTable>
        {
            CreateTestTable("Orders", "dbo")
        };

        var metadataLoader = new TestMetadataLoaderWithRules(new Dictionary<string, object?>
        {
            ["soft-delete"] = "file_value"
        });

        var model = DbModel.FromTables(tables, metadataLoader, Array.Empty<DbStoredProcedure>(), Array.Empty<DbForeignKey>(), additionalMetadata);

        var orders = model.GetTableFromDbName("Orders");
        orders.GetMetadataValue("soft-delete").Should().Be("db_override");
    }

    [Fact]
    public void DbModel_NullAdditionalMetadata_WorksNormally()
    {
        var tables = new List<DbTable>
        {
            CreateTestTable("Users", "dbo")
        };

        var metadataLoader = new TestNoOpMetadataLoader();
        var model = DbModel.FromTables(tables, metadataLoader, Array.Empty<DbStoredProcedure>(), Array.Empty<DbForeignKey>(), null);

        model.Tables.Should().HaveCount(1);
    }

    #endregion

    #region DatabaseMetadataSource

    [Fact]
    public void DatabaseMetadataSource_Priority_Is100()
    {
        var source = new DatabaseMetadataSource("Server=fake;Database=fake;");
        source.Priority.Should().Be(100);
    }

    [Fact]
    public void DatabaseMetadataSource_NullConnectionString_ThrowsArgumentNull()
    {
        var act = () => new DatabaseMetadataSource(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DatabaseMetadataSource_CustomTableName_Accepted()
    {
        var source = new DatabaseMetadataSource("Server=fake;Database=fake;", "_custom_config");
        source.Priority.Should().Be(100);
    }

    #endregion

    #region End-to-End Priority

    [Fact]
    public async Task EndToEnd_FileAndInMemory_InMemoryOverridesFile()
    {
        var fileSource = new FileMetadataSource(new[] { "dbo.Users { tenant-filter: file_tenant }" });
        var dbSource = new InMemoryMetadataSource(100, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.Users"] = new Dictionary<string, object?> { ["tenant-filter"] = "db_tenant" }
        });

        var composite = new CompositeMetadataSource(new IMetadataSource[] { fileSource, dbSource });
        var result = await composite.LoadTableMetadataAsync();

        result["dbo.Users"]["tenant-filter"].Should().Be("db_tenant");
    }

    [Fact]
    public async Task EndToEnd_FileAndInMemory_FileKeysPreservedIfNotOverridden()
    {
        var fileSource = new FileMetadataSource(new[]
        {
            "dbo.Users { tenant-filter: tenant_id; label: Name }"
        });
        var dbSource = new InMemoryMetadataSource(100, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.Users"] = new Dictionary<string, object?> { ["tenant-filter"] = "org_id" }
        });

        var composite = new CompositeMetadataSource(new IMetadataSource[] { fileSource, dbSource });
        var result = await composite.LoadTableMetadataAsync();

        result["dbo.Users"]["tenant-filter"].Should().Be("org_id");
        result["dbo.Users"]["label"].Should().Be("Name");
    }

    [Fact]
    public async Task EndToEnd_ThreeSources_HighestPriorityWins()
    {
        var low = new InMemoryMetadataSource(0, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.T"] = new Dictionary<string, object?> { ["key"] = "low" }
        });
        var mid = new InMemoryMetadataSource(50, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.T"] = new Dictionary<string, object?> { ["key"] = "mid" }
        });
        var high = new InMemoryMetadataSource(100, new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.T"] = new Dictionary<string, object?> { ["key"] = "high" }
        });

        var composite = new CompositeMetadataSource(new IMetadataSource[] { high, low, mid });
        var result = await composite.LoadTableMetadataAsync();

        result["dbo.T"]["key"].Should().Be("high");
    }

    #endregion

    #region Test Helpers

    private static DbTable CreateTestTable(string name, string schema)
    {
        var columns = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = new ColumnDto
            {
                ColumnName = "Id",
                GraphQlName = "Id",
                NormalizedName = "id",
                DataType = "int",
                IsPrimaryKey = true,
            }
        };

        return new DbTable
        {
            DbName = name,
            GraphQlName = name,
            NormalizedName = name,
            TableSchema = schema,
            TableType = "BASE TABLE",
            ColumnLookup = columns,
            GraphQlLookup = columns,
        };
    }

    private sealed class TestNoOpMetadataLoader : IMetadataLoader
    {
        public void ApplyDatabaseMetadata(IDictionary<string, object?> metadata, string rootName = ":root") { }
        public void ApplySchemaMetadata(IDbSchema schema, IDictionary<string, object?> metadata) { }
        public void ApplyTableMetadata(IDbTable table, IDictionary<string, object?> metadata) { }
        public void ApplyColumnMetadata(IDbTable table, ColumnDto column, IDictionary<string, object?> metadata) { }
    }

    private sealed class TestMetadataLoaderWithRules : IMetadataLoader
    {
        private readonly IDictionary<string, object?> _tableRules;

        public TestMetadataLoaderWithRules(IDictionary<string, object?> tableRules)
        {
            _tableRules = tableRules;
        }

        public void ApplyDatabaseMetadata(IDictionary<string, object?> metadata, string rootName = ":root") { }
        public void ApplySchemaMetadata(IDbSchema schema, IDictionary<string, object?> metadata) { }

        public void ApplyTableMetadata(IDbTable table, IDictionary<string, object?> metadata)
        {
            foreach (var (key, value) in _tableRules)
                metadata[key] = value;
        }

        public void ApplyColumnMetadata(IDbTable table, ColumnDto column, IDictionary<string, object?> metadata) { }
    }

    #endregion
}

/// <summary>
/// Test implementation of IMetadataSource backed by an in-memory dictionary.
/// </summary>
public sealed class InMemoryMetadataSource : IMetadataSource
{
    private readonly IDictionary<string, IDictionary<string, object?>> _data;

    public int Priority { get; }

    public InMemoryMetadataSource(int priority, IDictionary<string, IDictionary<string, object?>> data)
    {
        Priority = priority;
        _data = data;
    }

    public Task<IDictionary<string, IDictionary<string, object?>>> LoadTableMetadataAsync()
    {
        return Task.FromResult(_data);
    }
}
