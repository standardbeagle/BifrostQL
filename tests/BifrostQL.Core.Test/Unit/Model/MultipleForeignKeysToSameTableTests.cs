using BifrostQL.Core.Model;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Two foreign keys from one table to the same target — orders referencing
/// addresses twice, for billing and shipping — used to collapse into a single
/// link. The single-link dictionary was keyed by the PARENT TABLE's GraphQL
/// name, so the second FK hit a duplicate key and was skipped whole: the
/// surviving `addresses` field silently bound to whichever FK the database
/// happened to enumerate first, the other FK became unnavigable, and (because
/// the skip was a `continue`) the parent side lost its second child collection
/// too.
///
/// The fix names each link after its FK's own role instead. When a child
/// references a parent exactly once the field keeps the parent's name, which is
/// the overwhelmingly common case and stays source-compatible; when it
/// references it more than once, EVERY link to that parent is named for its
/// foreign-key column and the bare parent name is not emitted at all. Keeping
/// the bare name for one of them would preserve a field whose meaning depends
/// on database enumeration order — the defect, not a compatibility guarantee.
/// </summary>
public sealed class MultipleForeignKeysToSameTableTests
{
    [Fact]
    public void SingleForeignKey_KeepsTheParentTableName()
    {
        var model = BuildModel();
        new ForeignKeyRelationshipStrategy().DiscoverRelationships(model, new[] { CustomerFk() });

        var orders = model.GetTableFromDbName("orders");

        orders.SingleLinks.Values.Select(l => l.ParentFieldName)
            .Should().ContainSingle().Which.Should().Be("customers");
    }

    [Fact]
    public void TwoForeignKeysToSameTable_BothBecomeNavigable()
    {
        var model = BuildModel();
        new ForeignKeyRelationshipStrategy().DiscoverRelationships(model, AddressFks());

        var orders = model.GetTableFromDbName("orders");

        orders.SingleLinks.Values.Select(l => l.ParentFieldName)
            .Should().BeEquivalentTo(new[] { "billing_address", "shipping_address" });
    }

    [Fact]
    public void TwoForeignKeysToSameTable_EachLinkBindsItsOwnColumn()
    {
        var model = BuildModel();
        new ForeignKeyRelationshipStrategy().DiscoverRelationships(model, AddressFks());

        var orders = model.GetTableFromDbName("orders");
        var byField = orders.SingleLinks.Values.ToDictionary(l => l.ParentFieldName);

        // The whole point: the field a caller navigates determines which FK is
        // followed. Binding both to one column is the bug in a subtler costume.
        byField["billing_address"].ChildId.DbName.Should().Be("billing_address_id");
        byField["shipping_address"].ChildId.DbName.Should().Be("shipping_address_id");
    }

    [Fact]
    public void TwoForeignKeysToSameTable_DoNotEmitTheAmbiguousBareName()
    {
        var model = BuildModel();
        new ForeignKeyRelationshipStrategy().DiscoverRelationships(model, AddressFks());

        var orders = model.GetTableFromDbName("orders");

        orders.SingleLinks.Values.Select(l => l.ParentFieldName)
            .Should().NotContain("addresses");
    }

    [Fact]
    public void TwoForeignKeysToSameTable_ParentGetsBothChildCollections()
    {
        var model = BuildModel();
        new ForeignKeyRelationshipStrategy().DiscoverRelationships(model, AddressFks());

        var addresses = model.GetTableFromDbName("addresses");

        // The old `continue` skipped the multi-link too, so an address could only
        // ever reach the orders that referenced it through one of its two roles.
        addresses.MultiLinks.Values.Select(l => l.ChildFieldName)
            .Should().BeEquivalentTo(new[] { "orders_by_billing_address", "orders_by_shipping_address" });
    }

    [Fact]
    public void ForeignKeyOrder_DoesNotChangeTheFieldNames()
    {
        // Field names must come from the schema, not from the order the driver
        // happens to hand back constraints.
        var forward = BuildModel();
        new ForeignKeyRelationshipStrategy().DiscoverRelationships(forward, AddressFks());

        var reversed = BuildModel();
        new ForeignKeyRelationshipStrategy().DiscoverRelationships(reversed, AddressFks().Reverse().ToArray());

        var forwardNames = forward.GetTableFromDbName("orders").SingleLinks.Values
            .Select(l => $"{l.ParentFieldName}->{l.ChildId.DbName}").OrderBy(x => x);
        var reversedNames = reversed.GetTableFromDbName("orders").SingleLinks.Values
            .Select(l => $"{l.ParentFieldName}->{l.ChildId.DbName}").OrderBy(x => x);

        reversedNames.Should().BeEquivalentTo(forwardNames);
    }

    [Fact]
    public void RoleNameCollidingWithAColumn_IsDisambiguated()
    {
        // A table can already own a column called `billing_address`; the link
        // field shares the same GraphQL namespace, so it has to step aside.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithColumn("order_id", "int", isPrimaryKey: true)
                .WithColumn("billing_address", "nvarchar")
                .WithColumn("billing_address_id", "int")
                .WithColumn("shipping_address_id", "int"))
            .WithTable("addresses", t => t
                .WithColumn("address_id", "int", isPrimaryKey: true)
                .WithColumn("street", "nvarchar"))
            .Build();

        new ForeignKeyRelationshipStrategy().DiscoverRelationships(model, AddressFks());

        var orders = model.GetTableFromDbName("orders");
        var names = orders.SingleLinks.Values.Select(l => l.ParentFieldName).ToArray();

        names.Should().Contain("shipping_address");
        names.Should().NotContain("billing_address");
        names.Should().HaveCount(2);
    }

    private static IDbModel BuildModel() =>
        DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithColumn("order_id", "int", isPrimaryKey: true)
                .WithColumn("customer_id", "int")
                .WithColumn("billing_address_id", "int")
                .WithColumn("shipping_address_id", "int"))
            .WithTable("customers", t => t
                .WithColumn("customer_id", "int", isPrimaryKey: true)
                .WithColumn("name", "nvarchar"))
            .WithTable("addresses", t => t
                .WithColumn("address_id", "int", isPrimaryKey: true)
                .WithColumn("street", "nvarchar"))
            .Build();

    private static DbForeignKey CustomerFk() => Fk("FK_orders_customers", "customer_id", "customers", "customer_id");

    private static DbForeignKey[] AddressFks() => new[]
    {
        Fk("FK_orders_addresses_billing", "billing_address_id", "addresses", "address_id"),
        Fk("FK_orders_addresses_shipping", "shipping_address_id", "addresses", "address_id"),
    };

    private static DbForeignKey Fk(string name, string childColumn, string parentTable, string parentColumn) =>
        new()
        {
            ConstraintName = name,
            ChildTableSchema = "",
            ChildTableName = "orders",
            ChildColumnNames = new[] { childColumn },
            ParentTableSchema = "",
            ParentTableName = parentTable,
            ParentColumnNames = new[] { parentColumn },
        };
}
