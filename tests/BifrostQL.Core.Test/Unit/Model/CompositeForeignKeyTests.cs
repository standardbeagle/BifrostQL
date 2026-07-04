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
    public void ForeignKeyRelationshipStrategy_LinksCompositeFK_WithFullColumnLists()
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

        var parentKey = parentTable.GraphQlName;
        childTable.SingleLinks.Should().ContainKey(parentKey);
        var link = childTable.SingleLinks[parentKey];
        link.IsComposite.Should().BeTrue();
        link.ChildIds.Select(c => c.ColumnName).Should().Equal("CustomerId", "TenantId");
        link.ParentIds.Select(c => c.ColumnName).Should().Equal("Id", "TenantId");

        parentTable.MultiLinks.Should().ContainKey(childTable.GraphQlName);
        var multi = parentTable.MultiLinks[childTable.GraphQlName];
        multi.IsComposite.Should().BeTrue();
        multi.ChildIds.Should().HaveCount(2);
        multi.ParentIds.Should().HaveCount(2);
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

    [Fact]
    public void CompositeJoin_EmitsAndedOnClause_WithSuffixedJoinIds()
    {
        // Build a minimal model with a composite FK and emit the SQL for
        // a top-level "child { parents { … } }" join through SqlVisitor's
        // helpers. The inner DISTINCT must project suffixed `JoinId_<i>`
        // columns; the outer ON must AND every per-column equality;
        // the wrap `src_id_<i>` projection must match.
        var (model, _, _) = BuildTwoTableModel();
        new ForeignKeyRelationshipStrategy().DiscoverRelationships(model, new[]
        {
            new DbForeignKey
            {
                ConstraintName = "FK_Orders_Customers_Composite",
                ChildTableSchema = "",
                ChildTableName = "Orders",
                ChildColumnNames = new[] { "CustomerId", "TenantId" },
                ParentTableSchema = "",
                ParentTableName = "Customers",
                ParentColumnNames = new[] { "Id", "TenantId" },
            }
        });
        var orders = model.GetTableFromDbName("Orders");
        var customers = model.GetTableFromDbName("Customers");

        // Stand up GqlObjectQuery wiring by hand: parent Orders, link to Customers.
        var ordersQuery = new BifrostQL.Core.QueryModel.GqlObjectQuery
        {
            DbTable = orders,
            TableName = "Orders",
            SchemaName = "",
            GraphQlName = "Orders",
            Path = "Orders",
            QueryType = BifrostQL.Core.QueryModel.QueryType.Standard,
            ScalarColumns = { new BifrostQL.Core.QueryModel.GqlObjectColumn("OrderId") },
        };
        var customersLink = new BifrostQL.Core.QueryModel.GqlObjectQuery
        {
            DbTable = customers,
            TableName = "Customers",
            SchemaName = "",
            GraphQlName = customers.GraphQlName,
            Path = $"Orders->{customers.GraphQlName}",
            QueryType = BifrostQL.Core.QueryModel.QueryType.Single,
            ScalarColumns = { new BifrostQL.Core.QueryModel.GqlObjectColumn("Name") },
        };
        ordersQuery.Links.Add(customersLink);
        ordersQuery.ConnectLinks(model);

        var dialect = BifrostQL.SqlServer.SqlServerDialect.Instance;
        var parameters = new BifrostQL.Core.QueryModel.SqlParameterCollection();
        var sqls = new Dictionary<string, BifrostQL.Core.QueryModel.ParameterizedSql>();
        ordersQuery.AddSqlParameterized(model, dialect, sqls, parameters);

        var linkKey = $"Orders->{customers.GraphQlName}";
        sqls.Should().ContainKey(linkKey);
        var sql = sqls[linkKey].Sql;

        // Inner restricted DISTINCT projects suffixed JoinId aliases.
        sql.Should().Contain("[CustomerId] AS [JoinId_0]");
        sql.Should().Contain("[TenantId] AS [JoinId_1]");

        // Outer ON ANDs every per-column equality with suffix.
        sql.Should().Contain("[a].[JoinId_0] = [b].[Id]");
        sql.Should().Contain("[a].[JoinId_1] = [b].[TenantId]");
        sql.Should().Contain(" AND ");

        // Wrap projects suffixed src_id aliases.
        sql.Should().Contain("[a].[JoinId_0] [src_id_0]");
        sql.Should().Contain("[a].[JoinId_1] [src_id_1]");
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
