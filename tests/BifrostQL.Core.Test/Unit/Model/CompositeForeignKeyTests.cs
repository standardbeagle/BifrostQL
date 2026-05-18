using BifrostQL.Core.Model;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Coverage for the composite-FK foundation work:
///   - TableLinkDto carries ordered ChildIds/ParentIds lists with
///     single-column FKs producing exactly one entry (back-compat).
///   - TableJoin similarly defaults FromColumns/ConnectedColumns to a
///     singleton over the scalar FromColumn/ConnectedColumn.
///   - ForeignKeyRelationshipStrategy explicitly skips composite FKs
///     today (SQL emit + reader join-key matching are still single-column),
///     so a composite FK produces no entries in SingleLinks/MultiLinks.
///
/// When the SQL emitter and ReaderEnum learn multi-column join keys,
/// drop the composite skip in `ForeignKeyRelationshipStrategy` and add
/// tests asserting the link IS created with ChildIds.Count > 1.
/// </summary>
public sealed class CompositeForeignKeyTests
{
    [Fact]
    public void TableLinkDto_SingleColumn_DefaultsChildIdsParentIdsToSingleton()
    {
        var (model, _, _) = BuildTwoTableModel();
        var childTable = model.GetTableFromDbName("Orders");
        var parentTable = model.GetTableFromDbName("Customers");

        var link = new TableLinkDto
        {
            Name = "customers",
            ChildId = childTable.ColumnLookup["CustomerId"],
            ParentId = parentTable.ColumnLookup["Id"],
            ChildTable = childTable,
            ParentTable = parentTable,
        };

        link.ChildIds.Should().ContainSingle().Which.Should().Be(link.ChildId);
        link.ParentIds.Should().ContainSingle().Which.Should().Be(link.ParentId);
        link.IsComposite.Should().BeFalse();
    }

    [Fact]
    public void TableLinkDto_Composite_CarriesFullColumnLists()
    {
        var (model, _, _) = BuildTwoTableModel();
        var childTable = model.GetTableFromDbName("Orders");
        var parentTable = model.GetTableFromDbName("Customers");

        var link = new TableLinkDto
        {
            Name = "customers",
            ChildId = childTable.ColumnLookup["CustomerId"],
            ParentId = parentTable.ColumnLookup["Id"],
            ChildIds = new[]
            {
                childTable.ColumnLookup["CustomerId"],
                childTable.ColumnLookup["TenantId"],
            },
            ParentIds = new[]
            {
                parentTable.ColumnLookup["Id"],
                parentTable.ColumnLookup["TenantId"],
            },
            ChildTable = childTable,
            ParentTable = parentTable,
        };

        link.ChildIds.Should().HaveCount(2);
        link.ParentIds.Should().HaveCount(2);
        link.IsComposite.Should().BeTrue();
    }

    [Fact]
    public void ForeignKeyRelationshipStrategy_SkipsCompositeFKs_UntilSqlEmitterSupportsThem()
    {
        var (model, childTable, parentTable) = BuildTwoTableModel();
        var compositeFk = new DbForeignKey
        {
            ConstraintName = "FK_Orders_Customers_Composite",
            ChildTableSchema = "",
            ChildTableName = "Orders",
            ChildColumnNames = new[] { "CustomerId", "TenantId" },
            ParentTableSchema = "",
            ParentTableName = "Customers",
            ParentColumnNames = new[] { "Id", "TenantId" },
        };

        new ForeignKeyRelationshipStrategy().DiscoverRelationships(model, new[] { compositeFk });

        childTable.SingleLinks.Should().BeEmpty(
            "composite FKs are detected by the loader but not yet linked — the SQL emitter still produces single-column ON clauses");
        parentTable.MultiLinks.Should().BeEmpty();
    }

    [Fact]
    public void ForeignKeyRelationshipStrategy_SingleColumnFK_StillCreatesLink()
    {
        var (model, childTable, parentTable) = BuildTwoTableModel();
        var singleFk = new DbForeignKey
        {
            ConstraintName = "FK_Orders_Customers",
            ChildTableSchema = "",
            ChildTableName = "Orders",
            ChildColumnNames = new[] { "CustomerId" },
            ParentTableSchema = "",
            ParentTableName = "Customers",
            ParentColumnNames = new[] { "Id" },
        };

        new ForeignKeyRelationshipStrategy().DiscoverRelationships(model, new[] { singleFk });

        // Single-link key is the parent table's GraphQlName (pluralized
        // table name) — `customers`, not the raw DbName `Customers`.
        var parentKey = parentTable.GraphQlName;
        childTable.SingleLinks.Should().ContainKey(parentKey);
        var link = childTable.SingleLinks[parentKey];
        link.ChildId.ColumnName.Should().Be("CustomerId");
        link.ParentId.ColumnName.Should().Be("Id");
        link.ChildIds.Should().ContainSingle();
        link.ParentIds.Should().ContainSingle();
        link.IsComposite.Should().BeFalse();
    }

    private static (IDbModel model, IDbTable orders, IDbTable customers) BuildTwoTableModel()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithColumn("OrderId", "int", isPrimaryKey: true)
                .WithColumn("CustomerId", "int")
                .WithColumn("TenantId", "int"))
            .WithTable("Customers", t => t
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("TenantId", "int")
                .WithColumn("Name", "nvarchar"))
            .Build();
        return (model, model.GetTableFromDbName("Orders"), model.GetTableFromDbName("Customers"));
    }
}
