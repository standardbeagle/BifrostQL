using BifrostQL.Core.AppMetadata;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// RED/GREEN TDD validation of the app-metadata overlay against the Membership
/// Manager scenario. A Membership Manager application tracks four entities —
/// members, households, dues, and events — and these tests prove that every
/// presentation concern (entity labels, field-level forms, list grids, and
/// foreign-key relationships) can be described purely with overlay metadata.
///
/// The point of the scenario is that no hardcoded form or grid is needed: the
/// overlay alone carries enough structure for an SPA or React Native client to
/// render the application. The overlay also round-trips through the stable
/// camelCase JSON contract (<see cref="AppMetadataJson"/>) so the same
/// description ships to the client unchanged.
/// </summary>
public class MembershipManagerAppMetadataTests
{
    /// <summary>
    /// Builds the complete Membership Manager overlay describing all four
    /// entities. This is the single source the application would deploy; the
    /// tests below assert that it is sufficient on its own.
    /// </summary>
    private static AppMetadataModel BuildMembershipManagerOverlay()
    {
        var members = new EntityMetadata
        {
            Label = "Members",
            Icon = "person",
            NavPlacement = "main",
            DisplayFields = new[] { "first_name", "last_name" },
            Fields = new Dictionary<string, FieldMetadata>
            {
                ["first_name"] = new FieldMetadata { Widget = "text", Group = "Identity" },
                ["last_name"] = new FieldMetadata { Widget = "text", Group = "Identity" },
                ["email"] = new FieldMetadata
                {
                    Widget = "text",
                    Validation = "email",
                    Group = "Contact",
                    HelpText = "Primary contact email.",
                },
                ["join_date"] = new FieldMetadata { Widget = "datepicker", Group = "Membership" },
                ["status"] = new FieldMetadata { Widget = "select", Group = "Membership" },
            },
            Grid = new GridPresetMetadata
            {
                DefaultColumns = new[] { "first_name", "last_name", "email", "status" },
                DefaultSort = new[] { "last_name asc" },
                DefaultFilters = new[] { "status = active" },
                SavedViews = new Dictionary<string, SavedViewMetadata>
                {
                    ["active"] = new SavedViewMetadata
                    {
                        Name = "Active Members",
                        Filters = new[] { "status = active" },
                    },
                },
                BulkActions = new[] { "export", "email" },
            },
            Relationships = new Dictionary<string, RelationshipMetadata>
            {
                ["household"] = new RelationshipMetadata
                {
                    Label = "Household",
                    TargetEntity = "dbo.households",
                    Kind = RelationshipKind.ForeignKeySelector,
                    ForeignKeyField = "household_id",
                },
                ["dues"] = new RelationshipMetadata
                {
                    Label = "Dues",
                    TargetEntity = "dbo.dues",
                    Kind = RelationshipKind.ChildCollection,
                    ForeignKeyField = "member_id",
                    DisplayColumns = new[] { "period", "amount", "paid_on" },
                },
            },
        };

        var households = new EntityMetadata
        {
            Label = "Households",
            Icon = "home",
            NavPlacement = "main",
            DisplayFields = new[] { "name" },
            Fields = new Dictionary<string, FieldMetadata>
            {
                ["name"] = new FieldMetadata { Widget = "text", Group = "Identity" },
                ["address"] = new FieldMetadata { Widget = "text", Group = "Location" },
            },
            Grid = new GridPresetMetadata
            {
                DefaultColumns = new[] { "name", "address" },
                DefaultSort = new[] { "name asc" },
            },
            Relationships = new Dictionary<string, RelationshipMetadata>
            {
                ["members"] = new RelationshipMetadata
                {
                    Label = "Members",
                    TargetEntity = "dbo.members",
                    Kind = RelationshipKind.ChildCollection,
                    ForeignKeyField = "household_id",
                    DisplayColumns = new[] { "first_name", "last_name" },
                },
            },
        };

        var dues = new EntityMetadata
        {
            Label = "Dues",
            Icon = "receipt",
            NavPlacement = "finance",
            DisplayFields = new[] { "period" },
            Fields = new Dictionary<string, FieldMetadata>
            {
                ["period"] = new FieldMetadata { Widget = "text", Group = "Billing" },
                ["amount"] = new FieldMetadata { Widget = "number", Group = "Billing" },
                ["paid_on"] = new FieldMetadata { Widget = "datepicker", Group = "Billing" },
            },
            Grid = new GridPresetMetadata
            {
                DefaultColumns = new[] { "period", "amount", "paid_on" },
                DefaultSort = new[] { "period desc" },
            },
            Relationships = new Dictionary<string, RelationshipMetadata>
            {
                ["member"] = new RelationshipMetadata
                {
                    Label = "Member",
                    TargetEntity = "dbo.members",
                    Kind = RelationshipKind.ForeignKeySelector,
                    ForeignKeyField = "member_id",
                },
            },
        };

        var events = new EntityMetadata
        {
            Label = "Events",
            Icon = "calendar",
            NavPlacement = "main",
            DisplayFields = new[] { "title" },
            Fields = new Dictionary<string, FieldMetadata>
            {
                ["title"] = new FieldMetadata { Widget = "text", Group = "Details" },
                ["starts_at"] = new FieldMetadata { Widget = "datepicker", Group = "Schedule" },
                ["location"] = new FieldMetadata { Widget = "text", Group = "Details" },
            },
            Grid = new GridPresetMetadata
            {
                DefaultColumns = new[] { "title", "starts_at", "location" },
                DefaultSort = new[] { "starts_at asc" },
            },
            Relationships = new Dictionary<string, RelationshipMetadata>
            {
                ["host_household"] = new RelationshipMetadata
                {
                    Label = "Host Household",
                    TargetEntity = "dbo.households",
                    Kind = RelationshipKind.ForeignKeySelector,
                    ForeignKeyField = "host_household_id",
                },
            },
        };

        return new AppMetadataModel
        {
            Entities = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbo.members"] = members,
                ["dbo.households"] = households,
                ["dbo.dues"] = dues,
                ["dbo.events"] = events,
            },
        };
    }

    [Fact]
    public void Overlay_DescribesAllFourMembershipManagerEntities()
    {
        var overlay = BuildMembershipManagerOverlay();

        overlay.Entities.Keys.Should().BeEquivalentTo(
            "dbo.members", "dbo.households", "dbo.dues", "dbo.events");
        overlay.Entities["dbo.members"].Label.Should().Be("Members");
        overlay.Entities["dbo.households"].Label.Should().Be("Households");
        overlay.Entities["dbo.dues"].Label.Should().Be("Dues");
        overlay.Entities["dbo.events"].Label.Should().Be("Events");
    }

    [Fact]
    public void Overlay_DescribesEntityForms_WithoutHardcodedForm()
    {
        // The form for an entity is fully derived from its field metadata —
        // widget, group, validation, help text — so a client renders it
        // generically with no entity-specific form code.
        var overlay = BuildMembershipManagerOverlay();
        var members = overlay.Entities["dbo.members"];

        members.Fields.Should().ContainKeys(
            "first_name", "last_name", "email", "join_date", "status");
        members.Fields["email"].Widget.Should().Be("text");
        members.Fields["email"].Validation.Should().Be("email");
        members.Fields["email"].HelpText.Should().Be("Primary contact email.");
        members.Fields["join_date"].Widget.Should().Be("datepicker");
        members.Fields["status"].Widget.Should().Be("select");

        // Fields carry layout groups, so the form needs no hardcoded layout.
        members.Fields.Values.Select(f => f.Group).Should()
            .OnlyContain(g => g != null);
    }

    [Fact]
    public void Overlay_DescribesEntityGrids_WithoutHardcodedGrid()
    {
        // The list view for each entity is fully derived from its grid preset,
        // so a client renders the grid generically with no entity-specific grid
        // code.
        var overlay = BuildMembershipManagerOverlay();

        foreach (var (key, entity) in overlay.Entities)
        {
            entity.Grid.Should().NotBeNull(
                $"every Membership Manager entity ({key}) needs a grid preset");
            entity.Grid!.DefaultColumns.Should().NotBeEmpty();
            entity.Grid.DefaultSort.Should().NotBeEmpty();
        }

        var membersGrid = overlay.Entities["dbo.members"].Grid!;
        membersGrid.DefaultColumns.Should().ContainInOrder(
            "first_name", "last_name", "email", "status");
        membersGrid.SavedViews.Should().ContainKey("active");
        membersGrid.SavedViews["active"].Name.Should().Be("Active Members");
        membersGrid.BulkActions.Should().Contain("export");
    }

    [Fact]
    public void Overlay_DescribesForeignKeyRelationships_BetweenEntities()
    {
        // The four entities form a connected graph: members ↔ households,
        // members → dues, events → households. Every relationship target
        // resolves to another overlay entity by qualified table name.
        var overlay = BuildMembershipManagerOverlay();

        var memberToHousehold = overlay.Entities["dbo.members"].Relationships["household"];
        memberToHousehold.Kind.Should().Be(RelationshipKind.ForeignKeySelector);
        memberToHousehold.TargetEntity.Should().Be("dbo.households");
        memberToHousehold.ForeignKeyField.Should().Be("household_id");

        var memberToDues = overlay.Entities["dbo.members"].Relationships["dues"];
        memberToDues.Kind.Should().Be(RelationshipKind.ChildCollection);
        memberToDues.TargetEntity.Should().Be("dbo.dues");

        var eventToHousehold = overlay.Entities["dbo.events"].Relationships["host_household"];
        eventToHousehold.TargetEntity.Should().Be("dbo.households");

        // Every relationship target is itself a described entity.
        var allTargets = overlay.Entities.Values
            .SelectMany(e => e.Relationships.Values)
            .Select(r => r.TargetEntity);
        allTargets.Should().OnlyContain(t => overlay.Entities.ContainsKey(t!));
    }

    [Fact]
    public void Overlay_RoundTripsThroughStableJsonContract()
    {
        // The same description that drives the assertions above must survive
        // the camelCase JSON contract unchanged, since that JSON is exactly
        // what ships to the SPA / React Native client.
        var overlay = BuildMembershipManagerOverlay();

        var json = AppMetadataJson.Serialize(overlay);
        var restored = AppMetadataJson.Deserialize(json);

        json.Should().Contain("\"displayFields\"")
            .And.Contain("\"foreignKeyField\"")
            .And.Contain("\"foreignKeySelector\"");
        restored.Entities.Should().HaveCount(4);
        restored.Entities["dbo.members"].Grid!.DefaultColumns.Should()
            .ContainInOrder("first_name", "last_name", "email", "status");
        restored.Entities["dbo.members"].Relationships["dues"].Kind
            .Should().Be(RelationshipKind.ChildCollection);
    }
}
