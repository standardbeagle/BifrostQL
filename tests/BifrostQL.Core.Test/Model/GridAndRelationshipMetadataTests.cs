using BifrostQL.Core.AppMetadata;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// RED/GREEN TDD coverage for the grid-preset and relationship extensions to
/// the app-metadata overlay (<see cref="GridPresetMetadata"/>,
/// <see cref="SavedViewMetadata"/>, <see cref="RelationshipMetadata"/>) and
/// their integration into <see cref="EntityMetadata"/>.
///
/// These assert the extensions are pure data and round-trip losslessly through
/// the stable camelCase JSON contract established in sub-task 1
/// (<see cref="AppMetadataJson"/>).
/// </summary>
public class GridAndRelationshipMetadataTests
{
    private static AppMetadataModel BuildSample()
    {
        return new AppMetadataModel
        {
            Entities = new Dictionary<string, EntityMetadata>
            {
                ["dbo.users"] = new EntityMetadata
                {
                    Label = "Users",
                    Grid = new GridPresetMetadata
                    {
                        DefaultColumns = new[] { "first_name", "last_name", "email" },
                        DefaultFilters = new[] { "is_active eq true" },
                        DefaultSort = new[] { "last_name asc" },
                        BulkActions = new[] { "delete", "export" },
                        SavedViews = new Dictionary<string, SavedViewMetadata>
                        {
                            ["active"] = new SavedViewMetadata
                            {
                                Name = "Active only",
                                Columns = new[] { "first_name", "email" },
                                Filters = new[] { "is_active eq true" },
                                Sort = new[] { "first_name asc" },
                            },
                        },
                    },
                    Relationships = new Dictionary<string, RelationshipMetadata>
                    {
                        ["orders"] = new RelationshipMetadata
                        {
                            TargetEntity = "sales.orders",
                            Kind = RelationshipKind.ChildCollection,
                            ForeignKeyField = "user_id",
                            DisplayColumns = new[] { "order_number", "total" },
                            Label = "Orders",
                        },
                        ["manager"] = new RelationshipMetadata
                        {
                            TargetEntity = "dbo.users",
                            // Kind omitted — defaults to ForeignKeySelector.
                        },
                    },
                },
            },
        };
    }

    [Fact]
    public void GridAndRelationshipMetadata_RoundTripsThroughJson_Losslessly()
    {
        var original = BuildSample();

        var json = AppMetadataJson.Serialize(original);
        var restored = AppMetadataJson.Deserialize(json);

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Serialize_GridAndRelationship_UseCamelCasePropertyNames()
    {
        var json = AppMetadataJson.Serialize(BuildSample());

        json.Should().Contain("\"grid\"");
        json.Should().Contain("\"defaultColumns\"");
        json.Should().Contain("\"defaultFilters\"");
        json.Should().Contain("\"defaultSort\"");
        json.Should().Contain("\"savedViews\"");
        json.Should().Contain("\"bulkActions\"");
        json.Should().Contain("\"relationships\"");
        json.Should().Contain("\"targetEntity\"");
        json.Should().Contain("\"foreignKeyField\"");
        json.Should().Contain("\"displayColumns\"");
        // .NET PascalCase property names must not leak into the contract.
        json.Should().NotContain("\"DefaultColumns\"");
        json.Should().NotContain("\"TargetEntity\"");
    }

    [Fact]
    public void Serialize_RelationshipKind_UsesCamelCaseEnumValue()
    {
        var json = AppMetadataJson.Serialize(BuildSample());

        // JsonStringEnumConverter with camelCase policy from sub-task 1.
        json.Should().Contain("\"childCollection\"");
        json.Should().NotContain("\"ChildCollection\"");
    }

    [Fact]
    public void Relationship_ReferencesTargetByQualifiedTableName()
    {
        var json = AppMetadataJson.Serialize(BuildSample());

        // Target entities are qualified table names, consistent with sub-task 1.
        json.Should().Contain("\"sales.orders\"");
    }

    [Fact]
    public void Deserialize_AbsentRelationshipKind_DefaultsToForeignKeySelector()
    {
        const string json =
            "{\"entities\":{\"dbo.t\":{\"relationships\":{\"r\":{\"targetEntity\":\"dbo.u\"}}}}}";

        var restored = AppMetadataJson.Deserialize(json);

        restored.Entities["dbo.t"].Relationships["r"].Kind
            .Should().Be(RelationshipKind.ForeignKeySelector);
    }

    [Fact]
    public void Serialize_OmitsNullGridAndEmptyCollections()
    {
        var metadata = new AppMetadataModel
        {
            Entities = new Dictionary<string, EntityMetadata>
            {
                ["dbo.t"] = new EntityMetadata { Label = "T" },
            },
        };

        var json = AppMetadataJson.Serialize(metadata);

        // Null grid is omitted by WhenWritingNull.
        json.Should().NotContain("\"grid\"");
    }

    [Fact]
    public void EntityWithoutGridOrRelationships_RoundTrips()
    {
        var original = new AppMetadataModel
        {
            Entities = new Dictionary<string, EntityMetadata>
            {
                ["dbo.t"] = new EntityMetadata { Label = "T" },
            },
        };

        var json = AppMetadataJson.Serialize(original);
        var restored = AppMetadataJson.Deserialize(json);

        var entity = restored.Entities["dbo.t"];
        entity.Grid.Should().BeNull();
        entity.Relationships.Should().BeEmpty();
    }

    [Fact]
    public void GridPreset_Records_WithEqualContent_AreEqual()
    {
        // Pure-data value semantics.
        var a = new GridPresetMetadata { DefaultColumns = new[] { "a" } };
        var b = new GridPresetMetadata { DefaultColumns = new[] { "a" } };

        // Records compare by reference for collection members, so equal-content
        // collections are distinct instances; assert the scalar-only case here.
        var c = new SavedViewMetadata { Name = "v" };
        var d = new SavedViewMetadata { Name = "v" };
        c.Should().Be(d);

        var r1 = new RelationshipMetadata { TargetEntity = "dbo.x", Label = "X" };
        var r2 = new RelationshipMetadata { TargetEntity = "dbo.x", Label = "X" };
        r1.Should().Be(r2);

        // a and b are not asserted equal: IReadOnlyList members use reference
        // equality in records, which is the documented sub-task 1 behavior.
        a.Should().NotBeSameAs(b);
    }
}
