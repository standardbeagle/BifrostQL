using BifrostQL.Core.Model;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Nested-insert planning for polymorphic child links: a note inserted under a
/// parent must receive the parent's PK in its id column (FK propagation) and the
/// discriminator constant baked into its data, so it resolves back through the
/// same polymorphic collection.
/// </summary>
public class TreeSyncEnginePolymorphicTests
{
    private static IDbModel BuildModel()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("companies", t => t
                .WithPrimaryKey("company_id")
                .WithColumn("name", "nvarchar"))
            .WithTable("notes", t => t
                .WithPrimaryKey("note_id")
                .WithColumn("entity_type", "nvarchar")
                .WithColumn("entity_id", "int")
                .WithColumn("content", "nvarchar")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicTypeCol, "entity_type")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicIdCol, "entity_id")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicMap, "company=companies"))
            .Build();
        new PolymorphicRelationshipStrategy().DiscoverRelationships(model);
        return model;
    }

    [Fact]
    public void NestedPolymorphicChild_AssignsParentIdToDiscriminatorIdColumn()
    {
        var model = BuildModel();
        var engine = new TreeSyncEngine(model);
        var companies = model.GetTableFromDbName("companies");

        var submitted = new Dictionary<string, object?>
        {
            ["name"] = "Acme",
            ["notes"] = new List<Dictionary<string, object?>>
            {
                new() { ["content"] = "First note" },
            },
        };

        var ops = engine.ComputeOperations(companies, submitted, existing: null);

        var noteInsert = ops.First(op => op.Table.DbName == "notes");
        // entity_id is filled from the parent company's inserted PK.
        noteInsert.ForeignKeyAssignments.Should().ContainKey("entity_id");
        noteInsert.ForeignKeyAssignments["entity_id"].Should().Be("companies");
    }

    [Fact]
    public void NestedPolymorphicChild_StampsDiscriminatorConstant()
    {
        var model = BuildModel();
        var engine = new TreeSyncEngine(model);
        var companies = model.GetTableFromDbName("companies");

        var submitted = new Dictionary<string, object?>
        {
            ["name"] = "Acme",
            ["notes"] = new List<Dictionary<string, object?>>
            {
                new() { ["content"] = "First note" },
            },
        };

        var ops = engine.ComputeOperations(companies, submitted, existing: null);

        var noteInsert = ops.First(op => op.Table.DbName == "notes");
        // entity_type is stamped from the link's discriminator, not the client.
        noteInsert.Data.Should().ContainKey("entity_type");
        noteInsert.Data["entity_type"].Should().Be("company");
        noteInsert.Data["content"].Should().Be("First note");
    }

    [Fact]
    public void NestedPolymorphicChild_OrdersParentBeforeChild()
    {
        var model = BuildModel();
        var engine = new TreeSyncEngine(model);
        var companies = model.GetTableFromDbName("companies");

        var submitted = new Dictionary<string, object?>
        {
            ["name"] = "Acme",
            ["notes"] = new List<Dictionary<string, object?>>
            {
                new() { ["content"] = "a" },
                new() { ["content"] = "b" },
            },
        };

        var ops = engine.ComputeOperations(companies, submitted, existing: null);

        ops.Should().HaveCount(3);
        ops[0].Table.DbName.Should().Be("companies");
        ops[0].Depth.Should().Be(0);
        ops.Skip(1).Should().OnlyContain(op => op.Table.DbName == "notes" && op.Depth == 1);
    }
}
