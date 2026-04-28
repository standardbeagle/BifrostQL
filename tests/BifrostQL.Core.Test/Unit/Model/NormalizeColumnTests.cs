using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Tests for column name normalization used by the name-based table linking heuristic.
/// The heuristic matches column NormalizedName to table NormalizedName to create
/// SingleLinks/MultiLinks when no foreign key metadata is available.
///
/// Name-based linking runs inside DbModel.LinkTables() which requires the
/// BuildWithForeignKeys path (via WithForeignKey on the test fixture).
/// </summary>
public class NormalizeColumnTests
{
    // === Underscore-separated FK columns via name-based heuristic (no FK metadata) ===
    // These use a dummy FK to trigger the BuildWithForeignKeys path,
    // but the actual link under test comes from name-based normalization.

    [Theory]
    [InlineData("workspace_id", "workspaces")]
    [InlineData("member_id", "members")]
    [InlineData("project_id", "projects")]
    [InlineData("task_id", "tasks")]
    [InlineData("label_id", "labels")]
    [InlineData("section_id", "sections")]
    public void UnderscoreSeparated_FkColumn_CreatesLink(string fkColumn, string parentTable)
    {
        // Use a dummy FK on a separate unrelated column to trigger BuildWithForeignKeys,
        // so name-based heuristic runs for fkColumn
        var model = DbModelTestFixture.Create()
            .WithTable(parentTable, t => t
                .WithPrimaryKey($"{parentTable.TrimEnd('s')}_id")
                .WithColumn("name", "nvarchar"))
            .WithTable("child_table", t => t
                .WithPrimaryKey("child_table_id")
                .WithColumn(fkColumn, "int")
                .WithColumn("value", "nvarchar"))
            // Dummy FK on a non-existent table just to enter the FK build path
            .WithForeignKey("FK_dummy", "nonexistent", "col", "alsonotexist", "col")
            .Build();

        var child = model.GetTableFromDbName("child_table");
        child.SingleLinks.Should().ContainKey(parentTable,
            $"column '{fkColumn}' should link to table '{parentTable}' via name-based heuristic");
    }

    [Fact]
    public void UnderscoreSeparated_ProjectTracker_AllLinksDetected()
    {
        // Mirrors the project-tracker quickstart schema with real FK declarations
        var model = DbModelTestFixture.Create()
            .WithTable("workspaces", t => t
                .WithPrimaryKey("workspace_id")
                .WithColumn("name", "nvarchar"))
            .WithTable("members", t => t
                .WithPrimaryKey("member_id")
                .WithColumn("workspace_id", "int")
                .WithColumn("name", "nvarchar"))
            .WithTable("projects", t => t
                .WithPrimaryKey("project_id")
                .WithColumn("workspace_id", "int")
                .WithColumn("owner_id", "int", isNullable: true)
                .WithColumn("name", "nvarchar"))
            .WithTable("sections", t => t
                .WithPrimaryKey("section_id")
                .WithColumn("project_id", "int")
                .WithColumn("name", "nvarchar"))
            .WithTable("tasks", t => t
                .WithPrimaryKey("task_id")
                .WithColumn("project_id", "int")
                .WithColumn("section_id", "int")
                .WithColumn("assignee_id", "int", isNullable: true)
                .WithColumn("parent_task_id", "int", isNullable: true)
                .WithColumn("title", "nvarchar"))
            .WithTable("labels", t => t
                .WithPrimaryKey("label_id")
                .WithColumn("workspace_id", "int")
                .WithColumn("name", "nvarchar"))
            .WithTable("task_labels", t => t
                .WithPrimaryKey("task_label_id")
                .WithColumn("task_id", "int")
                .WithColumn("label_id", "int"))
            // FK declarations matching the DDL
            .WithForeignKey("FK_members_workspaces", "members", "workspace_id", "workspaces", "workspace_id")
            .WithForeignKey("FK_projects_workspaces", "projects", "workspace_id", "workspaces", "workspace_id")
            .WithForeignKey("FK_sections_projects", "sections", "project_id", "projects", "project_id")
            .WithForeignKey("FK_tasks_projects", "tasks", "project_id", "projects", "project_id")
            .WithForeignKey("FK_tasks_sections", "tasks", "section_id", "sections", "section_id")
            .WithForeignKey("FK_labels_workspaces", "labels", "workspace_id", "workspaces", "workspace_id")
            .WithForeignKey("FK_task_labels_tasks", "task_labels", "task_id", "tasks", "task_id")
            .WithForeignKey("FK_task_labels_labels", "task_labels", "label_id", "labels", "label_id")
            .Build();

        // members -> workspaces
        var members = model.GetTableFromDbName("members");
        members.SingleLinks.Should().ContainKey("workspaces");

        // projects -> workspaces
        var projects = model.GetTableFromDbName("projects");
        projects.SingleLinks.Should().ContainKey("workspaces");

        // sections -> projects
        var sections = model.GetTableFromDbName("sections");
        sections.SingleLinks.Should().ContainKey("projects");

        // tasks -> projects and tasks -> sections
        var tasks = model.GetTableFromDbName("tasks");
        tasks.SingleLinks.Should().ContainKey("projects");
        tasks.SingleLinks.Should().ContainKey("sections");

        // labels -> workspaces
        var labels = model.GetTableFromDbName("labels");
        labels.SingleLinks.Should().ContainKey("workspaces");

        // task_labels -> tasks and task_labels -> labels
        var taskLabels = model.GetTableFromDbName("task_labels");
        taskLabels.SingleLinks.Should().ContainKey("tasks");
        taskLabels.SingleLinks.Should().ContainKey("labels");

        // Multi-links: workspaces should have members, projects, labels as children
        var workspaces = model.GetTableFromDbName("workspaces");
        workspaces.MultiLinks.Should().ContainKey("members");
        workspaces.MultiLinks.Should().ContainKey("projects");
        workspaces.MultiLinks.Should().ContainKey("labels");
    }

    // === CamelCase FK columns still work (regression) ===

    [Theory]
    [InlineData("UserId", "Users")]
    [InlineData("ProductId", "Products")]
    [InlineData("OrderId", "Orders")]
    [InlineData("CategoryId", "Categories")]
    public void CamelCase_FkColumn_StillCreatesLink(string fkColumn, string parentTable)
    {
        var model = DbModelTestFixture.Create()
            .WithTable(parentTable, t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("ChildTable", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn(fkColumn, "int")
                .WithColumn("Value", "nvarchar"))
            // Dummy FK to trigger BuildWithForeignKeys for name-based linking
            .WithForeignKey("FK_dummy", "nonexistent", "col", "alsonotexist", "col")
            .Build();

        var child = model.GetTableFromDbName("ChildTable");
        child.SingleLinks.Should().ContainKey(parentTable,
            $"column '{fkColumn}' should link to table '{parentTable}' (CamelCase regression)");
    }

    // === Hyphen-separated FK columns (less common but valid) ===

    [Fact]
    public void HyphenSeparated_FkColumn_CreatesLink()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("users", t => t
                .WithPrimaryKey("user-id")
                .WithColumn("name", "nvarchar"))
            .WithTable("orders", t => t
                .WithPrimaryKey("order-id")
                .WithColumn("user-id", "int")
                .WithColumn("total", "decimal"))
            .WithForeignKey("FK_dummy", "nonexistent", "col", "alsonotexist", "col")
            .Build();

        var orders = model.GetTableFromDbName("orders");
        orders.SingleLinks.Should().ContainKey("users");
    }

    // === Edge cases ===

    [Fact]
    public void PlainId_Column_DoesNotCreateSpuriousLinks()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("users", t => t
                .WithPrimaryKey("id")
                .WithColumn("name", "nvarchar"))
            .WithTable("orders", t => t
                .WithPrimaryKey("id")
                .WithColumn("name", "nvarchar"))
            .WithForeignKey("FK_dummy", "nonexistent", "col", "alsonotexist", "col")
            .Build();

        var users = model.GetTableFromDbName("users");
        users.SingleLinks.Should().BeEmpty();
    }

    [Fact]
    public void SelfReferencing_UnderscoreSeparated_CreatesLink()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("tasks", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("task_id")
                .WithColumn("parent_task_id", "int", isNullable: true)
                .WithColumn("title", "nvarchar"))
            .WithForeignKey("FK_tasks_self", "tasks", "parent_task_id", "tasks", "task_id")
            .Build();

        var tasks = model.GetTableFromDbName("tasks");
        tasks.SingleLinks.Should().ContainKey("tasks",
            "parent_task_id should create a self-referencing link to tasks");
    }

    [Fact]
    public void EcommerceSchema_UnderscoreSeparated_AllLinksDetected()
    {
        // Mirrors the ecommerce quickstart schema
        var model = DbModelTestFixture.Create()
            .WithTable("categories", t => t
                .WithPrimaryKey("category_id")
                .WithColumn("name", "nvarchar"))
            .WithTable("customers", t => t
                .WithPrimaryKey("customer_id")
                .WithColumn("name", "nvarchar"))
            .WithTable("products", t => t
                .WithPrimaryKey("product_id")
                .WithColumn("category_id", "int")
                .WithColumn("name", "nvarchar"))
            .WithTable("orders", t => t
                .WithPrimaryKey("order_id")
                .WithColumn("customer_id", "int")
                .WithColumn("total", "decimal"))
            .WithTable("order_items", t => t
                .WithPrimaryKey("order_item_id")
                .WithColumn("order_id", "int")
                .WithColumn("product_id", "int")
                .WithColumn("quantity", "int"))
            .WithTable("reviews", t => t
                .WithPrimaryKey("review_id")
                .WithColumn("product_id", "int")
                .WithColumn("customer_id", "int")
                .WithColumn("rating", "int"))
            .WithForeignKey("FK_products_categories", "products", "category_id", "categories", "category_id")
            .WithForeignKey("FK_orders_customers", "orders", "customer_id", "customers", "customer_id")
            .WithForeignKey("FK_order_items_orders", "order_items", "order_id", "orders", "order_id")
            .WithForeignKey("FK_order_items_products", "order_items", "product_id", "products", "product_id")
            .WithForeignKey("FK_reviews_products", "reviews", "product_id", "products", "product_id")
            .WithForeignKey("FK_reviews_customers", "reviews", "customer_id", "customers", "customer_id")
            .Build();

        var products = model.GetTableFromDbName("products");
        products.SingleLinks.Should().ContainKey("categories");

        var orders = model.GetTableFromDbName("orders");
        orders.SingleLinks.Should().ContainKey("customers");

        var orderItems = model.GetTableFromDbName("order_items");
        orderItems.SingleLinks.Should().ContainKey("orders");
        orderItems.SingleLinks.Should().ContainKey("products");

        var reviews = model.GetTableFromDbName("reviews");
        reviews.SingleLinks.Should().ContainKey("products");
        reviews.SingleLinks.Should().ContainKey("customers");
    }
}
