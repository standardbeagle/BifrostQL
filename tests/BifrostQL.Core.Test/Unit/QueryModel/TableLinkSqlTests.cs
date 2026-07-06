using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Sqlite;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Direct coverage for <see cref="TableLinkSql"/>, the SQL-fragment builders moved
/// off <c>TableLinkDto</c> so the Model layer stays pure data. Previously these were
/// only exercised transitively through aggregate-column SQL tests.
/// </summary>
public sealed class TableLinkSqlTests
{
    private static readonly ISqlDialect Dialect = SqliteDialect.Instance;

    private static TableLinkDto CustomerOrderLink()
    {
        // Orders.customer_id (child / "many") -> Customers.Id (parent / "one").
        var model = DbModelTestFixture.Create()
            .WithTable("Customers", t => t.WithPrimaryKey("Id", "int"))
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id", "int")
                .WithColumn("customer_id", "int"))
            .WithSingleLink("Orders", "customer_id", "Customers", "Id", "customer")
            .Build();
        return model.GetTableFromDbName("Orders").SingleLinks["customer"];
    }

    [Fact]
    public void ManyToOne_ResolvesChildAsSourceAndParentAsDestination()
    {
        var link = CustomerOrderLink();

        TableLinkSql.SourceTableRef(link, Dialect, LinkDirection.ManyToOne).Should().Contain("Orders");
        TableLinkSql.DestTableRef(link, Dialect, LinkDirection.ManyToOne).Should().Contain("Customers");
        TableLinkSql.DestJoinColumn(link, LinkDirection.ManyToOne).Should().Be("Id");
    }

    [Fact]
    public void OneToMany_ResolvesParentAsSourceAndChildAsDestination()
    {
        var link = CustomerOrderLink();

        TableLinkSql.SourceTableRef(link, Dialect, LinkDirection.OneToMany).Should().Contain("Customers");
        TableLinkSql.DestTableRef(link, Dialect, LinkDirection.OneToMany).Should().Contain("Orders");
        TableLinkSql.DestJoinColumn(link, LinkDirection.OneToMany).Should().Be("customer_id");
    }

    [Fact]
    public void SourceColumns_QualifiesEscapesAndAliases()
    {
        var link = CustomerOrderLink();

        // ManyToOne source column is the child FK, qualified by the child table by default.
        TableLinkSql.SourceColumns(link, Dialect, LinkDirection.ManyToOne, columnName: "joinId")
            .Should().Be("\"Orders\".\"customer_id\" AS \"joinId\"");

        // An explicit table qualifier overrides the default.
        TableLinkSql.SourceColumns(link, Dialect, LinkDirection.ManyToOne, tableName: "next")
            .Should().Be("\"next\".\"customer_id\"");

        // OneToMany source column is the parent key.
        TableLinkSql.SourceColumns(link, Dialect, LinkDirection.OneToMany)
            .Should().Be("\"Customers\".\"Id\"");
    }
}
