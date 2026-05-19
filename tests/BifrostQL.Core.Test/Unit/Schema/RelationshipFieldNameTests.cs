using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Model;
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

    [Fact]
    public void TableTypeDefinition_UsesNumberedAliasWhenSelfFkChildrenNameAlreadyExists()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("categories", t => t
                .WithPrimaryKey("id")
                .WithColumn("parent_id", "int")
                .WithColumn("name"))
            .WithTable("categories_children", t => t
                .WithPrimaryKey("id")
                .WithColumn("category_id", "int")
                .WithColumn("name"))
            .WithSingleLink("categories", "parent_id", "categories", "id")
            .WithMultiLink("categories", "id", "categories_children", "category_id")
            .Build();
        var categories = model.Tables.Single(t => t.GraphQlName == "categories");
        var selfLink = new TableLinkDto
        {
            Name = "categories",
            ParentTable = categories,
            ChildTable = categories,
            ParentId = categories.ColumnLookup["id"],
            ChildId = categories.ColumnLookup["parent_id"],
            ChildFieldNameOverride = BifrostQL.Core.Model.Relationships.ForeignKeyRelationshipStrategy
                .ResolveChildFieldNameForTest(categories, categories),
        };
        categories.MultiLinks.Add(selfLink.ChildFieldName, selfLink);

        var sdl = new TableSchemaGenerator(categories).GetTableTypeDefinition(model, includeDynamicJoins: false);

        selfLink.ChildFieldName.Should().Be("categories_children_2");
        sdl.Should().Contain("categories_children(filter: TableFiltercategories_childrenInput) : [categories_children]");
        sdl.Should().Contain("categories_children_2(filter: TableFiltercategoriesInput) : [categories]");
    }
}
