using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// A table's link fields share the GraphQL field namespace with its columns. A link named after
/// the related table collided with a column of the same name (<c>invoice.customer</c> as both
/// an Int and the customer link), and the duplicate field broke the schema build. Every
/// strategy now names links through one taken-check that includes the columns.
/// </summary>
public sealed class LinkFieldColumnCollisionTests
{
    private static IReadOnlySet<string> FieldNames(GraphQL.Types.ISchema schema, string typeName) =>
        schema.AllTypes.OfType<GraphQL.Types.IComplexGraphType>()
            .Single(t => t.Name == typeName)
            .Fields.Select(f => f.Name)
            .ToHashSet(StringComparer.Ordinal);

    private static void ShouldNotShadowAColumn(IDbTable table, string linkKey)
    {
        table.Columns.Select(c => c.GraphQlName).Should().NotContain(linkKey,
            $"link field '{linkKey}' on {table.DbName} would duplicate a column field");
    }

    [Fact]
    public void SingleForeignKey_ParentNamedLikeAChildColumn_GetsADistinctFieldName()
    {
        // invoice.customer is the FK column itself; the parent table is also customer.
        var model = DbModelTestFixture.Create()
            .WithTable("customer", t => t.WithSchema("dbo").WithPrimaryKey("id"))
            .WithTable("invoice", t => t.WithSchema("dbo").WithPrimaryKey("id").WithColumn("customer", "int"))
            .WithForeignKey("FK_invoice_customer", "invoice", "customer", "customer", "id")
            .Build();

        var invoice = model.GetTableFromDbName("dbo", "invoice");
        var link = invoice.SingleLinks.Should().ContainSingle().Subject;
        ShouldNotShadowAColumn(invoice, link.Key);
        link.Value.ParentFieldName.Should().Be(link.Key, "the emitted field and the dictionary key are one name");

        var schema = DbSchema.FromModel(model);
        schema.Initialize();
        FieldNames(schema, "invoice").Should().Contain(new[] { "customer", link.Key });
    }

    [Fact]
    public void ChildCollection_NamedLikeAParentColumn_GetsADistinctFieldName()
    {
        // customer carries a column named invoice (say, a default invoice number), and invoice
        // is also the child table of customer.
        var model = DbModelTestFixture.Create()
            .WithTable("customer", t => t.WithSchema("dbo").WithPrimaryKey("id").WithColumn("invoice", "nvarchar"))
            .WithTable("invoice", t => t.WithSchema("dbo").WithPrimaryKey("id").WithColumn("customer_id", "int"))
            .WithForeignKey("FK_invoice_customer", "invoice", "customer_id", "customer", "id")
            .Build();

        var customer = model.GetTableFromDbName("dbo", "customer");
        var link = customer.MultiLinks.Should().ContainSingle().Subject;
        ShouldNotShadowAColumn(customer, link.Key);
        link.Value.ChildFieldName.Should().Be(link.Key);

        var schema = DbSchema.FromModel(model);
        schema.Initialize();
        FieldNames(schema, "customer").Should().Contain(new[] { "invoice", link.Key });
    }

    [Fact]
    public void ManyToMany_TargetNamedLikeASourceColumn_GetsADistinctFieldName()
    {
        // users has a denormalised roles column and a many-to-many to the roles table.
        var model = DbModelTestFixture.Create()
            .WithTable("users", t => t.WithSchema("dbo").WithPrimaryKey("id").WithColumn("roles", "nvarchar")
                .WithMetadata(MetadataKeys.Relationships.ManyToMany, "roles:assignments"))
            .WithTable("roles", t => t.WithSchema("dbo").WithPrimaryKey("id"))
            .WithTable("assignments", t => t.WithSchema("dbo").WithPrimaryKey("id")
                .WithColumn("users_id", "int").WithColumn("roles_id", "int"))
            .WithForeignKey("FK_a_users", "assignments", "users_id", "users", "id")
            .WithForeignKey("FK_a_roles", "assignments", "roles_id", "roles", "id")
            .Build();

        var users = model.GetTableFromDbName("dbo", "users");
        var link = users.ManyToManyLinks.Should().ContainSingle().Subject;
        ShouldNotShadowAColumn(users, link.Key);

        var schema = DbSchema.FromModel(model);
        schema.Initialize();
        FieldNames(schema, "users").Should().Contain(new[] { "roles", link.Key });
    }

    [Fact]
    public void SingleForeignKey_ParentNamedLikeAComputedColumn_GetsADistinctFieldName()
    {
        // invoice declares a computed column named customer next to its customer_id FK.
        var model = DbModelTestFixture.Create()
            .WithTable("customer", t => t.WithSchema("dbo").WithPrimaryKey("id"))
            .WithTable("invoice", t => t.WithSchema("dbo").WithPrimaryKey("id")
                .WithColumn("customer_id", "int")
                .WithColumn("label", "varchar")
                .WithMetadata(MetadataKeys.Computed.Expression, "customer:String:UPPER(label)"))
            .WithForeignKey("FK_invoice_customer", "invoice", "customer_id", "customer", "id")
            .Build();

        var invoice = model.GetTableFromDbName("dbo", "invoice");
        var link = invoice.SingleLinks.Should().ContainSingle().Subject;
        link.Key.Should().NotBe("customer", "the computed column already emits that field");

        var schema = DbSchema.FromModel(model);
        schema.Initialize();
        FieldNames(schema, "invoice").Should().Contain(new[] { "customer", link.Key });
    }
}
