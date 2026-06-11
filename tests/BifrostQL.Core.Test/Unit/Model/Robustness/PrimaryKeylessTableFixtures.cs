using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;

namespace BifrostQL.Core.Test.Model.Robustness;

/// <summary>
/// Shared helpers for tests that verify component robustness against tables that
/// have no primary key columns (e.g. log tables, heap tables, imported views).
///
/// These fixtures are reused by LookupTableDetector, NameBasedRelationshipStrategy,
/// PolymorphicRelationshipStrategy, SchemaGenerator, and DbModel metadata tests so
/// that all of them can exercise the same PK-less scenario without duplicating setup.
/// </summary>
public static class PrimaryKeylessTableFixtures
{
    /// <summary>
    /// A model containing exactly one table with zero primary key columns.
    /// </summary>
    public static IDbModel SinglePkLessTable() =>
        DbModelTestFixture.Create()
            .WithTable("audit_log", t => t
                .WithSchema("dbo")
                .WithColumn("logged_at", "datetime2")   // no isPrimaryKey
                .WithColumn("message", "nvarchar"))
            .Build();

    /// <summary>
    /// A model with one normally-keyed table and one PK-less table.
    /// Useful for strategies that iterate all tables and must not crash on the keyless one.
    /// </summary>
    public static IDbModel MixedModel() =>
        DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("audit_log", t => t
                .WithSchema("dbo")
                .WithColumn("logged_at", "datetime2")
                .WithColumn("message", "nvarchar"))
            .Build();

    /// <summary>
    /// A model where a polymorphic child's map references both a normal parent and
    /// a PK-less parent, to verify the polymorphic strategy skips the keyless one.
    /// </summary>
    public static IDbModel PolymorphicModelWithPkLessParent()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("companies", t => t
                .WithPrimaryKey("company_id")
                .WithColumn("name", "nvarchar"))
            .WithTable("no_pk_entity", t => t
                .WithColumn("code", "nvarchar")    // no PK
                .WithColumn("label", "nvarchar"))
            .WithTable("notes", t => t
                .WithPrimaryKey("note_id")
                .WithColumn("entity_type", "nvarchar")
                .WithColumn("entity_id", "int")
                .WithColumn("content", "nvarchar")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicTypeCol, "entity_type")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicIdCol, "entity_id")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicMap, "company=companies, nopk=no_pk_entity"))
            .Build();
        return model;
    }

    /// <summary>
    /// A no-op IMetadataLoader for use in DbModel.FromTables tests.
    /// </summary>
    public sealed class NoOpMetadataLoader : IMetadataLoader
    {
        public void ApplyDatabaseMetadata(IDictionary<string, object?> metadata, string rootName = ":root") { }
        public void ApplySchemaMetadata(IDbSchema schema, IDictionary<string, object?> metadata) { }
        public void ApplyTableMetadata(IDbTable table, IDictionary<string, object?> metadata) { }
        public void ApplyColumnMetadata(IDbTable table, ColumnDto column, IDictionary<string, object?> metadata) { }
    }
}
