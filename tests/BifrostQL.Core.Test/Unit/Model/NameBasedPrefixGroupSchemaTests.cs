using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Model;
using FluentAssertions;
using NSubstitute;
using System.Data.Common;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Prefix-group name matching (<c>app_order.customer_id</c> → <c>app_customer</c>) resolves
/// the parent with the same rule as the non-prefixed path: the child's own schema first,
/// then only a stripped name defined in exactly one schema. The pre-fix prefix lookup was
/// keyed by the stripped bare name, filled first-wins across schemas, so with
/// <c>sales</c> read before <c>archive</c>, <c>archive.app_order.customer_id</c> linked to
/// <c>sales.app_customer</c>.
/// </summary>
public sealed class NameBasedPrefixGroupSchemaTests
{
    private static DbTable MakeTable(string schema, string name, Action<DbModelTestFixture.TableBuilder> configure)
    {
        var builder = new DbModelTestFixture.TableBuilder(name)
            .WithSchema(schema)
            .WithGraphQlName($"{schema}_{name}");
        configure(builder);
        return builder.Build();
    }

    private static IDbModel Build(params DbTable[] tables)
    {
        var connFactory = Substitute.For<IDbConnFactory>();
        connFactory.GetConnection().Returns(Substitute.For<DbConnection>());
        connFactory.TypeMapper.Returns(SqlServerTypeMapper.Instance);
        var loader = new DbModelLoader(connFactory, new MetadataLoader(Array.Empty<string>()));
        var read = new SchemaData(
            new Dictionary<ColumnRef, List<ColumnConstraintDto>>(),
            Array.Empty<ColumnDto>(),
            tables.Cast<IDbTable>().ToList());
        return loader.BuildModel(read, new MetadataLoader(Array.Empty<string>()));
    }

    private static IEnumerable<IDbTable> ParentsOf(IDbModel model, string schema, string table) =>
        model.GetTableFromDbName(schema, table).SingleLinks.Values.Select(l => l.ParentTable);

    private static DbTable Customer(string schema) =>
        MakeTable(schema, "app_customer", t => t.WithPrimaryKey("id").WithColumn("name", "nvarchar"));

    private static DbTable Order(string schema) =>
        MakeTable(schema, "app_order", t => t.WithPrimaryKey("id").WithColumn("customer_id", "int"));

    private static DbTable Invoice(string schema) =>
        MakeTable(schema, "app_invoice", t => t.WithPrimaryKey("id").WithColumn("total", "int"));

    [Fact]
    public void Infer_PrefixedParentInOwnAndEarlierSchema_LinksWithinOwnSchema()
    {
        var model = Build(
            Customer("sales"), Order("sales"), Invoice("sales"),
            Customer("archive"), Order("archive"), Invoice("archive"));

        ParentsOf(model, "archive", "app_order").Should().ContainSingle()
            .Which.Should().BeSameAs(model.GetTableFromDbName("archive", "app_customer"));
        ParentsOf(model, "sales", "app_order").Should().ContainSingle()
            .Which.Should().BeSameAs(model.GetTableFromDbName("sales", "app_customer"));
        model.GetTableFromDbName("sales", "app_customer").MultiLinks.Values
            .Select(l => l.ChildTable)
            .Should().NotContain(model.GetTableFromDbName("archive", "app_order"),
                "the old target, sales.app_customer, must not gain archive's order as a child");
    }

    [Fact]
    public void Infer_PrefixedParentOnlyInTwoOtherSchemas_IsNotInferred()
    {
        var model = Build(
            Customer("sales"),
            Customer("archive"),
            Order("ops"), Invoice("ops"));

        ParentsOf(model, "ops", "app_order").Should().BeEmpty();
    }

    [Fact]
    public void Infer_PrefixedParentOnlyInOneOtherSchema_StillLinksAcross()
    {
        var model = Build(
            Customer("sales"),
            Order("ops"), Invoice("ops"));

        ParentsOf(model, "ops", "app_order").Should().ContainSingle()
            .Which.Should().BeSameAs(model.GetTableFromDbName("sales", "app_customer"));
    }
}
