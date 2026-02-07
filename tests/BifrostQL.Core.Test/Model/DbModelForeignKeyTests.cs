using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

public class DbModelForeignKeyTests
{
    [Fact]
    public void ForeignKey_BasicLink_CreatesSingleAndMultiLinks()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("Total", "decimal"))
            .WithForeignKey("FK_Orders_Users", "Orders", "UserId", "Users", "Id")
            .Build();

        var orders = model.GetTableFromDbName("Orders");
        var users = model.GetTableFromDbName("Users");

        orders.SingleLinks.Should().ContainKey("Users");
        orders.SingleLinks["Users"].ParentTable.DbName.Should().Be("Users");
        orders.SingleLinks["Users"].ChildTable.DbName.Should().Be("Orders");
        orders.SingleLinks["Users"].ParentId.ColumnName.Should().Be("Id");
        orders.SingleLinks["Users"].ChildId.ColumnName.Should().Be("UserId");

        users.MultiLinks.Should().ContainKey("Orders");
        users.MultiLinks["Orders"].ParentTable.DbName.Should().Be("Users");
        users.MultiLinks["Orders"].ChildTable.DbName.Should().Be("Orders");
    }

    [Fact]
    public void ForeignKey_PrecedenceOverNameBased_UsesColumnFromFK()
    {
        // The column "CreatorId" would NOT match via name-based heuristic
        // (it normalizes to "Creator", no table named "Creator").
        // The FK explicitly maps it to Users.Id.
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Posts", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("CreatorId", "int")
                .WithColumn("Title", "nvarchar"))
            .WithForeignKey("FK_Posts_Users", "Posts", "CreatorId", "Users", "Id")
            .Build();

        var posts = model.GetTableFromDbName("Posts");
        posts.SingleLinks.Should().ContainKey("Users");
        posts.SingleLinks["Users"].ChildId.ColumnName.Should().Be("CreatorId");
    }

    [Fact]
    public void ForeignKey_BlocksNameBasedForSameColumn()
    {
        // Orders.UserId would match Users via name-based heuristic.
        // FK explicitly maps Orders.UserId -> Users.Id.
        // The FK link should be the one created, and name-based should NOT
        // create a duplicate.
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("Total", "decimal"))
            .WithForeignKey("FK_Orders_Users", "Orders", "UserId", "Users", "Id")
            .Build();

        var orders = model.GetTableFromDbName("Orders");
        // Should have exactly one link to Users, created by FK
        orders.SingleLinks.Should().HaveCount(1);
        orders.SingleLinks.Should().ContainKey("Users");
    }

    [Fact]
    public void ForeignKey_CoexistsWithNameBased_ForDifferentColumns()
    {
        // FK links Orders.UserId -> Users.Id
        // Name-based should still link Orders.ProductId -> Products.Id
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Products", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("ProductId", "int")
                .WithColumn("Total", "decimal"))
            .WithForeignKey("FK_Orders_Users", "Orders", "UserId", "Users", "Id")
            .Build();

        var orders = model.GetTableFromDbName("Orders");
        // FK-based link to Users
        orders.SingleLinks.Should().ContainKey("Users");
        // Name-based link to Products (no FK for this column)
        orders.SingleLinks.Should().ContainKey("Products");
        orders.SingleLinks.Should().HaveCount(2);
    }

    [Fact]
    public void ForeignKey_SelfReferencing_CreatesLinks()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Employees", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("ManagerId", "int", isNullable: true))
            .WithForeignKey("FK_Employees_Manager", "Employees", "ManagerId", "Employees", "Id")
            .Build();

        var employees = model.GetTableFromDbName("Employees");
        // Self-referencing FK creates a SingleLink from Employees to Employees
        employees.SingleLinks.Should().ContainKey("Employees");
        employees.SingleLinks["Employees"].ChildId.ColumnName.Should().Be("ManagerId");
        employees.SingleLinks["Employees"].ParentId.ColumnName.Should().Be("Id");
        employees.SingleLinks["Employees"].ParentTable.DbName.Should().Be("Employees");
        employees.SingleLinks["Employees"].ChildTable.DbName.Should().Be("Employees");

        // Self-referencing also creates a MultiLink
        employees.MultiLinks.Should().ContainKey("Employees");
    }

    [Fact]
    public void ForeignKey_Composite_IsSkipped()
    {
        // Composite FKs cannot be represented as single-column TableLinkDto
        var model = DbModelTestFixture.Create()
            .WithTable("Tenants", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("TenantId")
                .WithColumn("RegionId", "int", isPrimaryKey: true))
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("TenantId", "int")
                .WithColumn("RegionId", "int")
                .WithColumn("Name", "nvarchar"))
            .WithForeignKey("FK_Users_Tenants",
                "dbo", "Users", new[] { "TenantId", "RegionId" },
                "dbo", "Tenants", new[] { "TenantId", "RegionId" })
            .Build();

        var users = model.GetTableFromDbName("Users");
        // Composite FK is skipped, no links created from it
        users.SingleLinks.Should().NotContainKey("Tenants");
    }

    [Fact]
    public void ForeignKey_NonExistentChildTable_IsSkipped()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithForeignKey("FK_Missing_Users", "NonExistent", "UserId", "Users", "Id")
            .Build();

        var users = model.GetTableFromDbName("Users");
        users.MultiLinks.Should().BeEmpty();
    }

    [Fact]
    public void ForeignKey_NonExistentParentTable_IsSkipped()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int"))
            .WithForeignKey("FK_Orders_Missing", "Orders", "UserId", "NonExistent", "Id")
            .Build();

        var orders = model.GetTableFromDbName("Orders");
        orders.SingleLinks.Should().BeEmpty();
    }

    [Fact]
    public void ForeignKey_NonExistentColumn_IsSkipped_NameBasedFallbackStillWorks()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int"))
            .WithForeignKey("FK_Orders_Users", "Orders", "BadColumnName", "Users", "Id")
            .Build();

        var orders = model.GetTableFromDbName("Orders");
        // FK with bad column name is skipped, but name-based fallback
        // still links Orders.UserId -> Users.Id
        orders.SingleLinks.Should().ContainKey("Users");
        orders.SingleLinks["Users"].ChildId.ColumnName.Should().Be("UserId");
    }

    [Fact]
    public void ForeignKey_NoForeignKeys_BehavesLikeOriginal()
    {
        // When no FKs are provided, name-based heuristic still works
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("Total", "decimal"))
            .WithForeignKey("FK_Dummy", "NonExistent", "Col", "AlsoNonExistent", "Col")
            .Build();

        // Name-based heuristic should link Orders.UserId -> Users.Id
        var orders = model.GetTableFromDbName("Orders");
        orders.SingleLinks.Should().ContainKey("Users");
    }

    [Fact]
    public void ForeignKey_MultipleFKsFromSameChildTable()
    {
        // Orders has FK to Users and FK to Products
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Products", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("OrderItems", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("ProductId", "int")
                .WithColumn("Quantity", "int"))
            .WithForeignKey("FK_OrderItems_Users", "OrderItems", "UserId", "Users", "Id")
            .WithForeignKey("FK_OrderItems_Products", "OrderItems", "ProductId", "Products", "Id")
            .Build();

        var orderItems = model.GetTableFromDbName("OrderItems");
        orderItems.SingleLinks.Should().HaveCount(2);
        orderItems.SingleLinks.Should().ContainKey("Users");
        orderItems.SingleLinks.Should().ContainKey("Products");

        var users = model.GetTableFromDbName("Users");
        users.MultiLinks.Should().ContainKey("OrderItems");

        var products = model.GetTableFromDbName("Products");
        products.MultiLinks.Should().ContainKey("OrderItems");
    }

    [Fact]
    public void ForeignKey_OverridesNameBasedWithDifferentParent()
    {
        // Column "StatusId" would match "Status" table via name-based heuristic.
        // FK says StatusId -> Lookups.Id instead.
        var model = DbModelTestFixture.Create()
            .WithTable("Status", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("Lookups", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Value", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("StatusId", "int")
                .WithColumn("Total", "decimal"))
            .WithForeignKey("FK_Orders_Lookups", "Orders", "StatusId", "Lookups", "Id")
            .Build();

        var orders = model.GetTableFromDbName("Orders");
        // FK should link to Lookups, NOT Status
        orders.SingleLinks.Should().ContainKey("Lookups");
        orders.SingleLinks.Should().NotContainKey("Status");
        orders.SingleLinks["Lookups"].ChildId.ColumnName.Should().Be("StatusId");
    }

    [Fact]
    public void DbForeignKey_IsComposite_ReturnsTrueForMultiColumn()
    {
        var fk = new DbForeignKey
        {
            ConstraintName = "FK_Test",
            ParentTableSchema = "dbo",
            ParentTableName = "Parent",
            ParentColumnNames = new[] { "Col1", "Col2" },
            ChildTableSchema = "dbo",
            ChildTableName = "Child",
            ChildColumnNames = new[] { "Col1", "Col2" },
        };
        fk.IsComposite.Should().BeTrue();
    }

    [Fact]
    public void DbForeignKey_IsComposite_ReturnsFalseForSingleColumn()
    {
        var fk = new DbForeignKey
        {
            ConstraintName = "FK_Test",
            ParentTableSchema = "dbo",
            ParentTableName = "Parent",
            ParentColumnNames = new[] { "Id" },
            ChildTableSchema = "dbo",
            ChildTableName = "Child",
            ChildColumnNames = new[] { "ParentId" },
        };
        fk.IsComposite.Should().BeFalse();
    }

    [Fact]
    public void DbForeignKey_ToString_FormatsCorrectly()
    {
        var fk = new DbForeignKey
        {
            ConstraintName = "FK_Orders_Users",
            ParentTableSchema = "dbo",
            ParentTableName = "Users",
            ParentColumnNames = new[] { "Id" },
            ChildTableSchema = "dbo",
            ChildTableName = "Orders",
            ChildColumnNames = new[] { "UserId" },
        };
        fk.ToString().Should().Be("FK[FK_Orders_Users] dbo.Orders(UserId) -> dbo.Users(Id)");
    }
}
