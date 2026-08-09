using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Auth;

/// <summary>
/// The shared schema-read projection every introspection surface funnels through
/// (.claude/rules/protocol-adapter-security.md invariant 4). These facts pin the three security
/// properties the five per-adapter copies independently discovered before consolidation — they
/// are properties of the DECISION, not preferences of any one wire:
///
/// <list type="number">
/// <item>a policy-denied table is indistinguishable from a non-existent one, so the filter never
/// converts a disclosure into an existence oracle;</item>
/// <item>a foreign-key edge survives only when BOTH ends — tables and participating columns —
/// are visible, so an edge cannot re-disclose a hidden table through a visible one;</item>
/// <item>column visibility is answerable for every place a column name appears (either spelling),
/// not only where the column list itself is emitted.</item>
/// </list>
///
/// <para>Plus the fail-closed rule: an unparseable policy excludes the table even for admin,
/// because <c>PolicyConfigCollector.FromTable</c> throws before the evaluator's admin bypass.</para>
/// </summary>
public class SchemaReadVisibilityTests
{
    private static IDictionary<string, object?> Ctx(string userId, params string[] roles) =>
        new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultUserIdContextKey] = userId,
            [MetadataKeys.Auth.DefaultRolesContextKey] = roles,
        };

    /// <summary>
    /// orders is readable but hides `secret_note`; ledger denies read outright (a policy present
    /// with no read grant); broken carries an unparseable policy.
    /// </summary>
    private static IDbModel Model() => DbModelTestFixture.Create()
        .WithTable("orders", t => t
            .WithSchema("dbo")
            .WithPrimaryKey("id")
            .WithColumn("customer_id", "int", graphQlName: "customerId")
            .WithColumn("secret_note", "nvarchar", graphQlName: "secretNote")
            .WithMetadata(MetadataKeys.Policy.Actions, "read")
            .WithMetadata(MetadataKeys.Policy.ReadDeny, "secret_note"))
        .WithTable("customers", t => t
            .WithSchema("dbo")
            .WithPrimaryKey("id")
            .WithColumn("name")
            .WithMetadata(MetadataKeys.Policy.Actions, "read"))
        .WithTable("ledger", t => t
            .WithSchema("dbo")
            .WithPrimaryKey("id")
            .WithColumn("amount", "decimal")
            .WithMetadata(MetadataKeys.Policy.Actions, "update"))
        .WithTable("broken", t => t
            .WithSchema("dbo")
            .WithPrimaryKey("id")
            .WithMetadata(MetadataKeys.Policy.Actions, "not-an-action"))
        .WithSingleLink("orders", "customer_id", "customers", "id", "customer")
        .WithMultiLink("customers", "id", "orders", "customer_id", "orders")
        .Build();

    private static VisibleTable Table(IReadOnlyList<VisibleTable> visible, string name) =>
        SchemaReadVisibility.Find(visible, name)
            ?? throw new InvalidOperationException($"'{name}' should be visible");

    [Fact]
    public void A_read_denied_table_is_omitted_from_the_projection()
    {
        var visible = SchemaReadVisibility.Project(Model(), Ctx("u1", "member"));

        visible.Select(v => v.Table.DbName).Should().BeEquivalentTo(new[] { "orders", "customers" },
            "ledger denies read and broken's policy cannot be parsed");
    }

    [Fact]
    public void A_denied_table_is_indistinguishable_from_a_non_existent_one()
    {
        var visible = SchemaReadVisibility.Project(Model(), Ctx("u1", "member"));

        var denied = SchemaReadVisibility.Find(visible, "ledger");
        var nonExistent = SchemaReadVisibility.Find(visible, "no_such_table");

        denied.Should().BeNull();
        nonExistent.Should().BeNull();
        denied.Should().Be(nonExistent,
            "a denied table must answer exactly as a table that does not exist, or the filter " +
            "becomes an existence oracle");
    }

    [Fact]
    public void An_unparseable_policy_excludes_the_table_even_for_admin()
    {
        var visible = SchemaReadVisibility.Project(
            Model(), Ctx("root", MetadataKeys.Policy.DefaultAdminRole));

        visible.Select(v => v.Table.DbName).Should().NotContain("broken",
            "FromTable throws before the evaluator's admin bypass can run — fail closed");
        visible.Select(v => v.Table.DbName).Should().Contain("ledger",
            "admin is otherwise unrestricted, so this is a policy check and not a blanket hide");
    }

    [Fact]
    public void A_read_denied_column_is_absent_and_unnameable_in_either_spelling()
    {
        var visible = SchemaReadVisibility.Project(Model(), Ctx("u1", "member"));
        var orders = Table(visible, "orders");

        orders.Columns.Select(c => c.DbName).Should().NotContain("secret_note");
        orders.HasColumn("secret_note").Should().BeFalse();
        orders.HasColumn("secretNote").Should().BeFalse(
            "a surface naming the column by its GraphQL spelling must get the same answer");
        orders.HasColumn("customer_id").Should().BeTrue();
        orders.HasColumn("customerId").Should().BeTrue();
        orders.HasColumn("CUSTOMER_ID").Should().BeTrue("column names match case-insensitively");
    }

    [Fact]
    public void Key_columns_are_the_visible_subset_of_the_key()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("t", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("tenant_id")
                .WithPrimaryKey("id")
                .WithColumn("payload")
                .WithMetadata(MetadataKeys.Policy.Actions, "read")
                .WithMetadata(MetadataKeys.Policy.ReadDeny, "tenant_id"))
            .Build();

        var visible = SchemaReadVisibility.Project(model, Ctx("u1", "member"));

        Table(visible, "t").KeyColumns.Select(c => c.DbName)
            .Should().BeEquivalentTo(new[] { "id" },
                "a hidden key column is omitted, never named, and the rest of a composite key survives");
    }

    [Fact]
    public void ProjectTable_returns_null_for_a_denied_table_and_the_readable_columns_otherwise()
    {
        var model = Model();
        var ledger = model.Tables.Single(t => t.DbName == "ledger");
        var orders = model.Tables.Single(t => t.DbName == "orders");
        var ctx = Ctx("u1", "member");

        SchemaReadVisibility.ProjectTable(ledger, ctx).Should().BeNull();
        SchemaReadVisibility.ProjectTable(orders, ctx)!.Columns.Select(c => c.DbName)
            .Should().BeEquivalentTo(new[] { "id", "customer_id" });
    }

    [Fact]
    public void A_link_survives_only_when_both_ends_are_visible()
    {
        var model = Model();
        var orders = model.Tables.Single(t => t.DbName == "orders");
        var link = orders.SingleLinks["customer"];

        var visible = SchemaReadVisibility.Project(model, Ctx("u1", "member"));
        SchemaReadVisibility.IsLinkVisible(link, visible).Should().BeTrue();

        var withoutParent = visible.Where(v => v.Table.DbName != "customers").ToList();
        SchemaReadVisibility.IsLinkVisible(link, withoutParent).Should().BeFalse(
            "an edge naming a hidden table re-discloses it through the visible child");
    }

    [Fact]
    public void A_link_is_dropped_when_a_participating_column_is_hidden()
    {
        // customer_id — the child side of the edge — is read-denied, so the edge cannot be
        // published even though both tables are visible.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("id")
                .WithColumn("customer_id", "int")
                .WithMetadata(MetadataKeys.Policy.Actions, "read")
                .WithMetadata(MetadataKeys.Policy.ReadDeny, "customer_id"))
            .WithTable("customers", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("id")
                .WithMetadata(MetadataKeys.Policy.Actions, "read"))
            .WithSingleLink("orders", "customer_id", "customers", "id", "customer")
            .Build();

        var visible = SchemaReadVisibility.Project(model, Ctx("u1", "member"));
        var link = model.Tables.Single(t => t.DbName == "orders").SingleLinks["customer"];

        visible.Should().HaveCount(2, "both tables are readable");
        SchemaReadVisibility.IsLinkVisible(link, visible).Should().BeFalse(
            "the edge would name a column the data path refuses to return");
    }
}
