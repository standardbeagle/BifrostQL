using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model.Robustness;

/// <summary>
/// Verifies that all five model-robustness fixes hold: tables without a primary key
/// must not crash model/schema build code that previously called .First() on KeyColumns.
///
/// All tests share the PK-less fixture helpers from <see cref="PrimaryKeylessTableFixtures"/>.
/// </summary>
public sealed class PrimaryKeylessRobustnessTests
{
    // ─── SchemaGenerator helper (mirrors PolymorphicLinkTests approach) ───────

    private static readonly MethodInfo SchemaTextFromModelMethod =
        typeof(DbSchema).Assembly
            .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
            .GetMethod("SchemaTextFromModel", BindingFlags.Static | BindingFlags.Public)!;

    private static string GetSchemaText(IDbModel model, bool includeDynamicJoins = false)
        => (string)SchemaTextFromModelMethod.Invoke(null, new object[] { model, includeDynamicJoins })!;

    // ─── FIX 2: LookupTableDetector ──────────────────────────────────────────

    [Fact]
    public void LookupTableDetector_IsLookupTable_PkLessTable_ReturnsFalse()
    {
        // Arrange
        var model = PrimaryKeylessTableFixtures.SinglePkLessTable();
        var table = model.GetTableFromDbName("audit_log");

        // Act
        var result = LookupTableDetector.IsLookupTable(table);

        // Assert — must not throw; PK-less tables are never lookup tables
        result.Should().BeFalse();
    }

    [Fact]
    public void LookupTableDetector_DetectColumnRoles_PkLessTable_DoesNotThrowAndReturnsEmptyId()
    {
        // Arrange — table with NO primary key column
        var model = PrimaryKeylessTableFixtures.SinglePkLessTable();
        var table = model.GetTableFromDbName("audit_log");

        // Act & Assert — must not throw InvalidOperationException from .First()
        var act = () => LookupTableDetector.DetectColumnRoles(table);
        act.Should().NotThrow();

        var roles = LookupTableDetector.DetectColumnRoles(table);
        roles.IdColumn.Should().Be(string.Empty, "a PK-less table has no id column");
    }

    // ─── FIX 3: NameBasedRelationshipStrategy ────────────────────────────────

    [Fact]
    public void NameBasedRelationshipStrategy_ModelWithPkLessTable_DoesNotThrow()
    {
        // Arrange — a model that has both a normal table and a PK-less table
        var model = PrimaryKeylessTableFixtures.MixedModel();

        // Act & Assert — strategy must skip the keyless table without throwing
        var strategy = new NameBasedRelationshipStrategy();
        var act = () => strategy.DiscoverRelationships(model, Array.Empty<DbForeignKey>());
        act.Should().NotThrow();
    }

    [Fact]
    public void NameBasedRelationshipStrategy_PkLessTable_NotAddedAsParent()
    {
        // Arrange — audit_log has no PK, so it cannot be a FK parent
        var model = PrimaryKeylessTableFixtures.MixedModel();
        var strategy = new NameBasedRelationshipStrategy();
        strategy.DiscoverRelationships(model, Array.Empty<DbForeignKey>());

        // audit_log should not appear in any table's SingleLinks as a parent
        foreach (var table in model.Tables)
        {
            table.SingleLinks.Values.Should().NotContain(
                link => link.ParentTable.DbName == "audit_log",
                "a PK-less table cannot be a valid FK parent");
        }
    }

    // ─── FIX 4: PolymorphicRelationshipStrategy ───────────────────────────────

    [Fact]
    public void PolymorphicRelationshipStrategy_PkLessParentCandidate_DoesNotThrow()
    {
        // Arrange — notes maps to both a normal parent and a PK-less parent
        var model = PrimaryKeylessTableFixtures.PolymorphicModelWithPkLessParent();

        // Act & Assert — strategy must skip no_pk_entity without throwing
        var strategy = new PolymorphicRelationshipStrategy();
        var act = () => strategy.DiscoverRelationships(model);
        act.Should().NotThrow();
    }

    [Fact]
    public void PolymorphicRelationshipStrategy_PkLessParentCandidate_NormalParentStillLinked()
    {
        // Arrange
        var model = PrimaryKeylessTableFixtures.PolymorphicModelWithPkLessParent();
        new PolymorphicRelationshipStrategy().DiscoverRelationships(model);

        // The keyed parent (companies) gets its notes link; the PK-less parent is skipped
        model.GetTableFromDbName("companies").MultiLinks.Should().ContainKey("notes",
            "the normally-keyed parent should still receive the polymorphic notes link");
        model.GetTableFromDbName("no_pk_entity").MultiLinks.Should().BeEmpty(
            "a PK-less table cannot be a polymorphic parent");
    }

    // ─── FIX 1: SchemaGenerator enum-table lookup ────────────────────────────

    [Fact]
    public void SchemaGenerator_EnumTableMissingFromModel_SkipsWithoutCrash()
    {
        // Arrange: build an EnumColumnMap that references a 'statuses' enum table,
        // then put it on a DbModel that does NOT contain 'statuses' in Tables.
        // This simulates a table being removed/hidden after the enum map was built.

        // Step 1: build a model that HAS 'statuses' so EnumColumnMap.Build can run
        var modelWithStatus = DbModelTestFixture.Create()
            .WithTable("statuses", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Code", "varchar")
                .WithMetadata(EnumTableConfig.MetadataKey, "Code"))
            .Build();

        var entries = EnumValueSanitizer.SanitizeAll(new[] { "active", "inactive" });
        var enumValues = new Dictionary<string, IReadOnlyList<EnumValueEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            ["statuses"] = entries
        };
        var resolvedCols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["statuses"] = "Code"
        };
        var orphanedEnumMap = EnumColumnMap.Build(modelWithStatus, enumValues, resolvedCols);

        // Step 2: build a DbModel WITHOUT 'statuses'
        var modelWithoutStatus = new DbModel
        {
            Tables = new List<IDbTable>(),
            Metadata = new Dictionary<string, object?>(),
            StoredProcedures = Array.Empty<DbStoredProcedure>(),
            EnumColumns = orphanedEnumMap,   // has 'statuses' key, but Tables is empty
        };

        // Act & Assert — must NOT throw "Sequence contains no elements";
        // the fix uses FirstOrDefault + continue to skip the missing table.
        var act = () => GetSchemaText(modelWithoutStatus);
        act.Should().NotThrow<InvalidOperationException>(
            "SchemaGenerator must skip enum tables that are absent from the model, " +
            "not crash with \"Sequence contains no elements\"");
    }

    // ─── FIX 5: DbModel.ApplyAdditionalMetadata case-insensitivity ───────────

    [Fact]
    public void DbModel_ApplyAdditionalMetadata_CasingMismatch_AppliesMetadata()
    {
        // Arrange: metadata key uses uppercase schema/table; table is lowercase
        var table = new DbTable
        {
            DbName = "users",
            GraphQlName = "users",
            NormalizedName = "user",
            TableSchema = "dbo",
            TableType = "BASE TABLE",
            ColumnLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = new ColumnDto
                {
                    ColumnName = "id", GraphQlName = "id",
                    NormalizedName = "id", DataType = "int", IsPrimaryKey = true
                }
            },
            GraphQlLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = new ColumnDto
                {
                    ColumnName = "id", GraphQlName = "id",
                    NormalizedName = "id", DataType = "int", IsPrimaryKey = true
                }
            },
        };

        // Metadata key is "DBO.USERS" — casing differs from "dbo.users"
        var additionalMetadata = new Dictionary<string, IDictionary<string, object?>>(StringComparer.Ordinal)
        {
            ["DBO.USERS"] = new Dictionary<string, object?> { ["tenant-filter"] = "tenant_id" }
        };

        // Act: DbModel.FromTables with the casing-mismatched additional metadata
        var model = DbModel.FromTables(
            new List<DbTable> { table },
            new PrimaryKeylessTableFixtures.NoOpMetadataLoader(),
            Array.Empty<DbStoredProcedure>(),
            Array.Empty<DbForeignKey>(),
            additionalMetadata);

        // Assert: the metadata should have been applied despite the casing difference
        var usersTable = model.GetTableFromDbName("users");
        usersTable.GetMetadataValue("tenant-filter").Should().Be("tenant_id",
            "ApplyAdditionalMetadata must use OrdinalIgnoreCase so casing differences do not silently drop metadata");
    }

    [Fact]
    public void DbModel_ApplyAdditionalMetadata_MatchingCase_AppliesMetadata()
    {
        // Arrange — baseline: matching case should still work after the fix
        var table = new DbTable
        {
            DbName = "Orders",
            GraphQlName = "Orders",
            NormalizedName = "Order",
            TableSchema = "dbo",
            TableType = "BASE TABLE",
            ColumnLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase)
            {
                ["Id"] = new ColumnDto
                {
                    ColumnName = "Id", GraphQlName = "Id",
                    NormalizedName = "id", DataType = "int", IsPrimaryKey = true
                }
            },
            GraphQlLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase)
            {
                ["Id"] = new ColumnDto
                {
                    ColumnName = "Id", GraphQlName = "Id",
                    NormalizedName = "id", DataType = "int", IsPrimaryKey = true
                }
            },
        };

        var additionalMetadata = new Dictionary<string, IDictionary<string, object?>>(StringComparer.Ordinal)
        {
            ["dbo.Orders"] = new Dictionary<string, object?> { ["soft-delete"] = "deleted_at" }
        };

        // Act
        var model = DbModel.FromTables(
            new List<DbTable> { table },
            new PrimaryKeylessTableFixtures.NoOpMetadataLoader(),
            Array.Empty<DbStoredProcedure>(),
            Array.Empty<DbForeignKey>(),
            additionalMetadata);

        // Assert
        var ordersTable = model.GetTableFromDbName("Orders");
        ordersTable.GetMetadataValue("soft-delete").Should().Be("deleted_at");
    }
}
