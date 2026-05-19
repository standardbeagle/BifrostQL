using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Schema;

public class RelationshipFieldNameTests
{
    [Fact]
    public void TableTypeDefinition_EmitsOrdinaryMultiLinkField()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("users", t => t
                .WithPrimaryKey("id")
                .WithColumn("name"))
            .WithTable("orders", t => t
                .WithPrimaryKey("id")
                .WithColumn("user_id", "int"))
            .WithMultiLink("users", "id", "orders", "user_id")
            .Build();
        var users = model.Tables.Single(t => t.GraphQlName == "users");

        var sdl = new TableSchemaGenerator(users).GetTableTypeDefinition(model, includeDynamicJoins: false);
        var aggregate = new TableSchemaGenerator(users).GetAggregateLinkDefinitions();

        sdl.Should().Contain("orders(filter: TableFilterordersInput) : [orders]");
        aggregate.Should().Contain("orders : orders_AggregateValue");
    }

    [Fact]
    public void TableTypeDefinition_AliasesSelfReferentialChildCollection()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("categories", t => t
                .WithPrimaryKey("id")
                .WithColumn("parent_id", "int")
                .WithColumn("name"))
            .WithSingleLink("categories", "parent_id", "categories", "id")
            .WithMultiLink("categories", "id", "categories", "parent_id")
            .Build();
        var categories = model.Tables.Single(t => t.GraphQlName == "categories");

        var sdl = new TableSchemaGenerator(categories).GetTableTypeDefinition(model, includeDynamicJoins: false);
        var aggregate = new TableSchemaGenerator(categories).GetAggregateLinkDefinitions();

        sdl.Should().Contain("categories : categories");
        sdl.Should().Contain("categories_children(filter: TableFiltercategoriesInput) : [categories]");
        aggregate.Should().Contain("categories : categories_AggregateValue");
        aggregate.Should().Contain("categories_children : categories_AggregateValue");
    }
}
