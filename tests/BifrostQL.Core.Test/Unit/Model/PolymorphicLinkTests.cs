using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Tests for the polymorphic-child relationship strategy: a shared child table
/// (e.g. notes with entity_type/entity_id) surfaced as a distinct navigable
/// collection on each mapped parent, isolated by a discriminator predicate.
/// </summary>
public sealed class PolymorphicLinkTests
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    private static readonly MethodInfo SchemaTextFromModelMethod = typeof(DbSchema).Assembly
        .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
        .GetMethod("SchemaTextFromModel", BindingFlags.Static | BindingFlags.Public)!;

    private static string GetSchemaText(IDbModel model)
        => (string)SchemaTextFromModelMethod.Invoke(null, new object[] { model, false })!;

    /// <summary>
    /// A CRM-like model: companies, contacts, deals and a shared polymorphic
    /// notes table mapping entity_type values to those parents.
    /// </summary>
    private static IDbModel BuildCrmModel(string map = "company=companies, contact=contacts, deal=deals")
    {
        var model = DbModelTestFixture.Create()
            .WithTable("companies", t => t
                .WithPrimaryKey("company_id")
                .WithColumn("name", "nvarchar"))
            .WithTable("contacts", t => t
                .WithPrimaryKey("contact_id")
                .WithColumn("first_name", "nvarchar"))
            .WithTable("deals", t => t
                .WithPrimaryKey("deal_id")
                .WithColumn("title", "nvarchar"))
            .WithTable("notes", t => t
                .WithPrimaryKey("note_id")
                .WithColumn("entity_type", "nvarchar")
                .WithColumn("entity_id", "int")
                .WithColumn("content", "nvarchar")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicTypeCol, "entity_type")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicIdCol, "entity_id")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicMap, map))
            .Build();

        new PolymorphicRelationshipStrategy().DiscoverRelationships(model);
        return model;
    }

    #region Strategy

    [Fact]
    public void Strategy_AddsMultiLink_ToEachMappedParent()
    {
        var model = BuildCrmModel();

        model.GetTableFromDbName("companies").MultiLinks.Should().ContainKey("notes");
        model.GetTableFromDbName("contacts").MultiLinks.Should().ContainKey("notes");
        model.GetTableFromDbName("deals").MultiLinks.Should().ContainKey("notes");
    }

    [Fact]
    public void Strategy_LinkCarriesDiscriminatorPredicate()
    {
        var model = BuildCrmModel();

        var companyNotes = model.GetTableFromDbName("companies").MultiLinks["notes"];
        companyNotes.TypePredicate.Should().NotBeNull();
        companyNotes.TypePredicate!.Column.ColumnName.Should().Be("entity_type");
        companyNotes.TypePredicate.Value.Should().Be("company");
        companyNotes.ChildTable.DbName.Should().Be("notes");
        companyNotes.ChildId.ColumnName.Should().Be("entity_id");
        companyNotes.ParentId.ColumnName.Should().Be("company_id");

        // Each parent gets its own discriminator value.
        model.GetTableFromDbName("contacts").MultiLinks["notes"].TypePredicate!.Value.Should().Be("contact");
        model.GetTableFromDbName("deals").MultiLinks["notes"].TypePredicate!.Value.Should().Be("deal");
    }

    [Fact]
    public void Strategy_UnknownParent_IsSkipped()
    {
        // 'lead' maps to a table that does not exist; the other two still link.
        var model = BuildCrmModel("company=companies, lead=leads, deal=deals");

        model.GetTableFromDbName("companies").MultiLinks.Should().ContainKey("notes");
        model.GetTableFromDbName("deals").MultiLinks.Should().ContainKey("notes");
        model.Tables.Should().NotContain(t => t.DbName == "leads");
    }

    [Fact]
    public void Strategy_MissingMetadata_CreatesNoLinks()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("companies", t => t.WithPrimaryKey("company_id").WithColumn("name", "nvarchar"))
            .WithTable("notes", t => t
                .WithPrimaryKey("note_id")
                .WithColumn("entity_type", "nvarchar")
                .WithColumn("entity_id", "int"))
            .Build();

        new PolymorphicRelationshipStrategy().DiscoverRelationships(model);

        model.GetTableFromDbName("companies").MultiLinks.Should().BeEmpty();
    }

    [Fact]
    public void Strategy_CompositeKeyParent_IsSkipped()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("companies", t => t
                .WithColumn("company_id", "int", isPrimaryKey: true)
                .WithColumn("tenant_id", "int", isPrimaryKey: true)
                .WithColumn("name", "nvarchar"))
            .WithTable("notes", t => t
                .WithPrimaryKey("note_id")
                .WithColumn("entity_type", "nvarchar")
                .WithColumn("entity_id", "int")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicTypeCol, "entity_type")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicIdCol, "entity_id")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicMap, "company=companies"))
            .Build();

        new PolymorphicRelationshipStrategy().DiscoverRelationships(model);

        // Composite-key parent cannot be addressed by a single entity_id column.
        model.GetTableFromDbName("companies").MultiLinks.Should().BeEmpty();
    }

    [Fact]
    public void Strategy_ExistingFieldName_IsDeduplicated()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("companies", t => t.WithPrimaryKey("company_id").WithColumn("name", "nvarchar"))
            .WithTable("notes", t => t
                .WithPrimaryKey("note_id")
                .WithColumn("entity_type", "nvarchar")
                .WithColumn("entity_id", "int")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicTypeCol, "entity_type")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicIdCol, "entity_id")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicMap, "company=companies"))
            .Build();

        // Pre-seed a conflicting "notes" multi-link.
        var companies = model.GetTableFromDbName("companies");
        var notes = model.GetTableFromDbName("notes");
        companies.MultiLinks["notes"] = new TableLinkDto
        {
            Name = "notes",
            ParentTable = companies,
            ParentId = companies.KeyColumns.First(),
            ChildTable = notes,
            ChildId = notes.ColumnLookup["entity_id"],
        };

        new PolymorphicRelationshipStrategy().DiscoverRelationships(model);

        // The polymorphic link gets a suffixed name rather than clobbering.
        companies.MultiLinks.Should().ContainKey("notes_2");
        companies.MultiLinks["notes_2"].TypePredicate.Should().NotBeNull();
    }

    #endregion

    #region Schema

    [Fact]
    public void Schema_EachParentType_ExposesNotesField()
    {
        var model = BuildCrmModel();

        var schema = GetSchemaText(model);

        schema.Should().Contain("type companies {");
        schema.Should().Contain("type contacts {");
        schema.Should().Contain("type deals {");
        // The polymorphic notes collection is emitted like any multi-link.
        schema.Should().Contain("notes(filter: TableFilternotesInput) : [notes]");
    }

    #endregion
}
