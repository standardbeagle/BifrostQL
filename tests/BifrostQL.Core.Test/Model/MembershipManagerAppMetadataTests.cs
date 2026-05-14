using BifrostQL.Core.AppMetadata;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// RED/GREEN TDD validation of the Membership Manager app-metadata overlay
/// JSON file (<c>samples/HostedSpa/membership-manager.appmetadata.json</c>).
/// This is the single overlay the HostedSpa sample deploys; the host wires it
/// via <see cref="FileAppMetadataSource"/> and serves it at <c>/_app-metadata</c>.
///
/// The tests prove that the shipped JSON file round-trips through the stable
/// camelCase contract (<see cref="AppMetadataJson"/>) and carries enough
/// structure — entity labels, field-level forms, list grids, foreign-key
/// relationships, and permission/visibility flags — for a metadata-driven
/// client to render the members + households screens with no hardcoded form
/// or grid.
/// </summary>
public class MembershipManagerAppMetadataTests
{
    /// <summary>
    /// Locates the overlay JSON file by walking up from the test assembly's
    /// base directory to the repository root, then into the HostedSpa sample.
    /// </summary>
    private static string OverlayFilePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BifrostQL.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test must run inside the BifrostQL repository");

        var path = Path.Combine(
            dir!.FullName, "samples", "HostedSpa", "membership-manager.appmetadata.json");
        File.Exists(path).Should().BeTrue($"the overlay file must exist at {path}");
        return path;
    }

    /// <summary>
    /// Deserializes the shipped overlay JSON file through the stable contract.
    /// </summary>
    private static AppMetadataModel LoadOverlay()
    {
        var json = File.ReadAllText(OverlayFilePath());
        return AppMetadataJson.Deserialize(json);
    }

    [Fact]
    public void OverlayFile_DeserializesAndDescribesMembersHouseholdsAndHouseholdMembers()
    {
        var overlay = LoadOverlay();

        overlay.Entities.Keys.Should().BeEquivalentTo(
            "main.members", "main.households", "main.household_members",
            "main.membership_plans", "main.member_memberships",
            "main.dues_invoices", "main.dues_payments");
        overlay.Entities["main.members"].Label.Should().Be("Members");
        overlay.Entities["main.households"].Label.Should().Be("Households");
        overlay.Entities["main.household_members"].Label.Should().Be("Household Members");
    }

    [Fact]
    public void OverlayFile_DescribesFinancialEntities_WithFieldsMatchingSchemaColumns()
    {
        // The four MM Financial tables (membership-manager.sql) are exposed as
        // overlay entities so a metadata-driven client renders their forms
        // generically, keyed by the actual SQL column names.
        var overlay = LoadOverlay();

        var plans = overlay.Entities["main.membership_plans"];
        plans.Label.Should().Be("Membership Plans");
        plans.Fields.Should().ContainKeys(
            "name", "description", "billing_period", "price_cents",
            "is_active", "tenant_id");

        var memberships = overlay.Entities["main.member_memberships"];
        memberships.Label.Should().Be("Member Memberships");
        memberships.Fields.Should().ContainKeys(
            "member_id", "plan_id", "start_date", "end_date", "status",
            "tenant_id");

        var invoices = overlay.Entities["main.dues_invoices"];
        invoices.Label.Should().Be("Dues Invoices");
        invoices.Fields.Should().ContainKeys(
            "member_id", "member_membership_id", "amount_cents", "issued_on",
            "due_on", "status", "tenant_id");

        var payments = overlay.Entities["main.dues_payments"];
        payments.Label.Should().Be("Dues Payments");
        payments.Fields.Should().ContainKeys(
            "invoice_id", "amount_cents", "paid_on", "method", "tenant_id");

        // Every financial field carries a layout group, matching the
        // members/households convention.
        foreach (var key in new[]
                 {
                     "main.membership_plans", "main.member_memberships",
                     "main.dues_invoices", "main.dues_payments",
                 })
        {
            overlay.Entities[key].Fields.Values
                .Select(f => f.Group).Should().OnlyContain(g => g != null);
        }
    }

    [Fact]
    public void OverlayFile_GatesFinancialAdminFields_WithVisibilityFlags()
    {
        // tenant_id stays admin-only and read-only across the financial
        // entities, consistent with the members/households convention.
        var overlay = LoadOverlay();

        foreach (var key in new[]
                 {
                     "main.membership_plans", "main.member_memberships",
                     "main.dues_invoices", "main.dues_payments",
                 })
        {
            var tenant = overlay.Entities[key].Fields["tenant_id"];
            tenant.Visible.Should().BeFalse($"{key}.tenant_id is admin-only");
            tenant.ReadOnly.Should().BeTrue($"{key}.tenant_id is system-managed");
        }
    }

    [Fact]
    public void OverlayFile_DescribesFinancialRelationships_ForFkFreeEditing()
    {
        // The financial entities chain member → membership → plan and
        // invoice → payment via fk-lookup relationship metadata.
        var overlay = LoadOverlay();

        var memberships = overlay.Entities["main.member_memberships"];
        memberships.Relationships["member"].TargetEntity.Should().Be("main.members");
        memberships.Relationships["member"].ForeignKeyField.Should().Be("member_id");
        memberships.Relationships["plan"].TargetEntity.Should().Be("main.membership_plans");
        memberships.Relationships["plan"].ForeignKeyField.Should().Be("plan_id");
        memberships.Relationships["plan"].Kind.Should().Be(RelationshipKind.ForeignKeySelector);

        var invoices = overlay.Entities["main.dues_invoices"];
        invoices.Relationships["member"].TargetEntity.Should().Be("main.members");
        invoices.Relationships["membership"].TargetEntity.Should().Be("main.member_memberships");
        invoices.Relationships["payments"].TargetEntity.Should().Be("main.dues_payments");
        invoices.Relationships["payments"].Kind.Should().Be(RelationshipKind.ChildCollection);

        var payments = overlay.Entities["main.dues_payments"];
        payments.Relationships["invoice"].TargetEntity.Should().Be("main.dues_invoices");
        payments.Relationships["invoice"].ForeignKeyField.Should().Be("invoice_id");
    }

    [Fact]
    public void OverlayFile_DescribesMemberFields_MatchingTheSchemaColumns()
    {
        // Field metadata is keyed by the actual membership-manager.sql column
        // names so a client renders the members form generically.
        var overlay = LoadOverlay();
        var members = overlay.Entities["main.members"];

        members.Fields.Should().ContainKeys(
            "first_name", "last_name", "email", "phone", "status",
            "joined_on", "household_id", "user_id", "tenant_id", "deleted_at");
        members.Fields["email"].Widget.Should().Be("text");
        members.Fields["email"].Validation.Should().Be("email");
        members.Fields["status"].Widget.Should().Be("select");
        members.Fields["joined_on"].Widget.Should().Be("datepicker");

        // Every field carries a layout group, so the form needs no hardcoded layout.
        members.Fields.Values.Select(f => f.Group).Should().OnlyContain(g => g != null);
    }

    [Fact]
    public void OverlayFile_GatesAdminOnlyFields_WithVisibilityFlags()
    {
        // Admin-only / system-managed fields carry visible:false so the form
        // sub-task can permission-gate them. user_id, tenant_id, and the
        // deleted_at soft-delete audit field are not shown to ordinary users.
        var overlay = LoadOverlay();
        var members = overlay.Entities["main.members"];

        members.Fields["user_id"].Visible.Should().BeFalse();
        members.Fields["tenant_id"].Visible.Should().BeFalse();
        members.Fields["deleted_at"].Visible.Should().BeFalse();
        members.Fields["deleted_at"].ReadOnly.Should().BeTrue();

        // Ordinary contact fields stay visible.
        members.Fields["first_name"].Visible.Should().BeTrue();
        members.Fields["email"].Visible.Should().BeTrue();
    }

    [Fact]
    public void OverlayFile_DescribesGridPresets_WithoutHardcodedGrid()
    {
        var overlay = LoadOverlay();

        foreach (var (key, entity) in overlay.Entities)
        {
            entity.Grid.Should().NotBeNull(
                $"every Membership Manager entity ({key}) needs a grid preset");
            entity.Grid!.DefaultColumns.Should().NotBeEmpty();
            entity.Grid.DefaultSort.Should().NotBeEmpty();
        }

        var membersGrid = overlay.Entities["main.members"].Grid!;
        membersGrid.DefaultColumns.Should().ContainInOrder(
            "first_name", "last_name", "email", "phone", "status");
        membersGrid.SavedViews.Should().ContainKey("active");
        membersGrid.SavedViews["active"].Name.Should().Be("Active Members");
        membersGrid.BulkActions.Should().Contain("export");
    }

    [Fact]
    public void OverlayFile_DescribesForeignKeyRelationships_ForFkFreeEditing()
    {
        // household_members links members and households via fk-lookup
        // relationship metadata, so the relationship can be edited without
        // hand-wiring foreign keys.
        var overlay = LoadOverlay();

        var memberToHousehold = overlay.Entities["main.members"].Relationships["household"];
        memberToHousehold.Kind.Should().Be(RelationshipKind.ForeignKeySelector);
        memberToHousehold.TargetEntity.Should().Be("main.households");
        memberToHousehold.ForeignKeyField.Should().Be("household_id");

        var householdMembers = overlay.Entities["main.household_members"];
        householdMembers.Relationships["household"].TargetEntity
            .Should().Be("main.households");
        householdMembers.Relationships["household"].ForeignKeyField
            .Should().Be("household_id");
        householdMembers.Relationships["member"].TargetEntity
            .Should().Be("main.members");
        householdMembers.Relationships["member"].ForeignKeyField
            .Should().Be("member_id");
        householdMembers.Fields["household_id"].Widget.Should().Be("fk-lookup");
        householdMembers.Fields["member_id"].Widget.Should().Be("fk-lookup");

        // Every relationship target is itself a described entity.
        var allTargets = overlay.Entities.Values
            .SelectMany(e => e.Relationships.Values)
            .Select(r => r.TargetEntity);
        allTargets.Should().OnlyContain(t => overlay.Entities.ContainsKey(t!));
    }

    [Fact]
    public void OverlayFile_RoundTripsThroughStableJsonContract()
    {
        // The shipped file must survive the camelCase JSON contract unchanged,
        // since that JSON is exactly what /_app-metadata serves to the client.
        var overlay = LoadOverlay();

        var json = AppMetadataJson.Serialize(overlay);
        var restored = AppMetadataJson.Deserialize(json);

        json.Should().Contain("\"displayFields\"")
            .And.Contain("\"foreignKeyField\"")
            .And.Contain("\"foreignKeySelector\"");
        restored.Entities.Should().HaveCount(7);
        restored.Entities["main.members"].Grid!.DefaultColumns.Should()
            .ContainInOrder("first_name", "last_name", "email", "phone", "status");
        restored.Entities["main.household_members"].Relationships["member"].Kind
            .Should().Be(RelationshipKind.ForeignKeySelector);
    }
}
