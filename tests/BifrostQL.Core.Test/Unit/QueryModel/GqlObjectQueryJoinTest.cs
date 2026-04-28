using FluentAssertions;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Tests for GqlObjectQuery join and link functionality.
/// Covers ConnectLinks, ToConnectedSqlParameterized, and GetRestrictedSqlParameterized.
/// </summary>
public sealed class GqlObjectQueryJoinTest
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    #region ConnectLinks - Single Links (ManyToOne)

    [Fact]
    public void ConnectLinks_SingleLink_CreatesJoinWithSingleQueryType()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var ordersTable = dbModel.GetTableFromDbName("Orders");

        var userLink = new GqlObjectQuery
        {
            GraphQlName = "user",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = ordersTable,
            TableName = "Orders",
            GraphQlName = "Orders",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Total") },
            Links = { userLink }
        };

        // Act
        query.ConnectLinks(dbModel);

        // Assert
        query.Joins.Should().ContainSingle();
        var join = query.Joins[0];
        join.Name.Should().Be("user");
        join.QueryType.Should().Be(QueryType.Single);
        join.ConnectedTable.TableName.Should().Be("Users");
        join.FromColumn.Should().Be("UserId");
        join.ConnectedColumn.Should().Be("Id");
    }

    [Fact]
    public void ConnectLinks_SingleLink_SetsLinkTableName()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var ordersTable = dbModel.GetTableFromDbName("Orders");

        var userLink = new GqlObjectQuery
        {
            GraphQlName = "user",
            ScalarColumns = { new GqlObjectColumn("Id") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = ordersTable,
            TableName = "Orders",
            GraphQlName = "Orders",
            Links = { userLink }
        };

        // Act
        query.ConnectLinks(dbModel);

        // Assert
        userLink.TableName.Should().Be("Users");
    }

    #endregion

    #region ConnectLinks - Multi Links (OneToMany)

    [Fact]
    public void ConnectLinks_MultiLink_CreatesJoinWithJoinQueryType()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var usersTable = dbModel.GetTableFromDbName("Users");

        var ordersLink = new GqlObjectQuery
        {
            GraphQlName = "orders",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Total") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            GraphQlName = "Users",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") },
            Links = { ordersLink }
        };

        // Act
        query.ConnectLinks(dbModel);

        // Assert
        query.Joins.Should().ContainSingle();
        var join = query.Joins[0];
        join.Name.Should().Be("orders");
        join.QueryType.Should().Be(QueryType.Join);
        join.ConnectedTable.TableName.Should().Be("Orders");
        join.FromColumn.Should().Be("Id");
        join.ConnectedColumn.Should().Be("UserId");
    }

    [Fact]
    public void ConnectLinks_MultiLink_SetsLinkTableName()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var usersTable = dbModel.GetTableFromDbName("Users");

        var ordersLink = new GqlObjectQuery
        {
            GraphQlName = "orders"
        };

        var query = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            GraphQlName = "Users",
            Links = { ordersLink }
        };

        // Act
        query.ConnectLinks(dbModel);

        // Assert
        ordersLink.TableName.Should().Be("Orders");
    }

    #endregion

    #region ConnectLinks - Alias Handling

    [Fact]
    public void ConnectLinks_WithAlias_PreservesAliasInJoin()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var usersTable = dbModel.GetTableFromDbName("Users");

        var ordersLink = new GqlObjectQuery
        {
            GraphQlName = "orders",
            Alias = "recentOrders"
        };

        var query = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            GraphQlName = "Users",
            Links = { ordersLink }
        };

        // Act
        query.ConnectLinks(dbModel);

        // Assert
        query.Joins[0].Alias.Should().Be("recentOrders");
    }

    #endregion

    #region ConnectLinks - Nested Links

    [Fact]
    public void ConnectLinks_NestedLinks_ConnectsAllLevels()
    {
        // Arrange
        var dbModel = StandardTestFixtures.CompanyHierarchy();
        var companiesTable = dbModel.GetTableFromDbName("Companies");

        var employeesLink = new GqlObjectQuery
        {
            GraphQlName = "employees"
        };

        var departmentsLink = new GqlObjectQuery
        {
            GraphQlName = "departments",
            Links = { employeesLink }
        };

        var query = new GqlObjectQuery
        {
            DbTable = companiesTable,
            TableName = "Companies",
            GraphQlName = "Companies",
            Links = { departmentsLink }
        };

        // Act
        query.ConnectLinks(dbModel);

        // Assert
        query.Joins.Should().ContainSingle();
        var departmentJoin = query.Joins[0];
        departmentJoin.Name.Should().Be("departments");
        departmentJoin.ConnectedTable.Joins.Should().ContainSingle();
        var employeeJoin = departmentJoin.ConnectedTable.Joins[0];
        employeeJoin.Name.Should().Be("employees");
    }

    #endregion

    #region ConnectLinks - Error Handling

    [Fact]
    public void ConnectLinks_InvalidLinkName_ThrowsExecutionError()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");

        var invalidLink = new GqlObjectQuery
        {
            GraphQlName = "nonExistentLink"
        };

        var query = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            GraphQlName = "Users",
            Links = { invalidLink }
        };

        // Act & Assert
        Action act = () => query.ConnectLinks(dbModel);
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*Unable to find join*nonExistentLink*");
    }

    #endregion

    #region ToConnectedSqlParameterized Tests

    [Fact]
    public void ToConnectedSqlParameterized_GeneratesInnerJoin()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var ordersTable = dbModel.GetTableFromDbName("Orders");

        var connectedQuery = new GqlObjectQuery
        {
            DbTable = ordersTable,
            TableName = "Orders",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Total") }
        };

        var tableJoin = new TableJoin
        {
            Name = "orders",
            FromColumn = "Id",
            ConnectedColumn = "UserId",
            Operator = "_eq",
            QueryType = QueryType.Join,
            ConnectedTable = connectedQuery
        };

        var mainSql = new ParameterizedSql(
            "SELECT DISTINCT [Id] AS [JoinId] FROM [Users]",
            new List<SqlParameterInfo>());

        var parameters = new SqlParameterCollection();

        // Act
        var result = GqlObjectQuery.ToConnectedSqlParameterized(dbModel, Dialect, parameters, mainSql, tableJoin);

        // Assert
        result.Sql.Should().Contain("INNER JOIN");
        result.Sql.Should().Contain("[Orders]");
        result.Sql.Should().Contain("[b].[UserId]");
    }

    [Fact]
    public void ToConnectedSqlParameterized_SingleQueryType_NoFilterOrPagination()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var usersTable = dbModel.GetTableFromDbName("Users");

        var connectedQuery = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") },
            Filter = TableFilter.FromObject(new Dictionary<string, object?>
            {
                { "Name", new Dictionary<string, object?> { { "_eq", "John" } } }
            }, "Users")
        };

        var tableJoin = new TableJoin
        {
            Name = "user",
            FromColumn = "UserId",
            ConnectedColumn = "Id",
            Operator = "_eq",
            QueryType = QueryType.Single,
            ConnectedTable = connectedQuery
        };

        var mainSql = new ParameterizedSql(
            "SELECT DISTINCT [UserId] AS [JoinId] FROM [Orders]",
            new List<SqlParameterInfo>());

        var parameters = new SqlParameterCollection();

        // Act
        var result = GqlObjectQuery.ToConnectedSqlParameterized(dbModel, Dialect, parameters, mainSql, tableJoin);

        // Assert
        // For Single queries, filter and pagination are NOT applied
        result.Sql.Should().NotContain("WHERE");
    }

    [Fact]
    public void ToConnectedSqlParameterized_JoinQueryType_AppliesFilterAndPagination()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var ordersTable = dbModel.GetTableFromDbName("Orders");

        var connectedQuery = new GqlObjectQuery
        {
            DbTable = ordersTable,
            TableName = "Orders",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Total") },
            Filter = TableFilter.FromObject(new Dictionary<string, object?>
            {
                { "Total", new Dictionary<string, object?> { { "_gt", 100 } } }
            }, "Orders"),
            Sort = new List<string> { "Total_desc" },
            Limit = 10
        };

        var tableJoin = new TableJoin
        {
            Name = "orders",
            FromColumn = "Id",
            ConnectedColumn = "UserId",
            Operator = "_eq",
            QueryType = QueryType.Join,
            ConnectedTable = connectedQuery
        };

        var mainSql = new ParameterizedSql(
            "SELECT DISTINCT [Id] AS [JoinId] FROM [Users]",
            new List<SqlParameterInfo>());

        var parameters = new SqlParameterCollection();

        // Act
        var result = GqlObjectQuery.ToConnectedSqlParameterized(dbModel, Dialect, parameters, mainSql, tableJoin);

        // Assert
        result.Sql.Should().Contain("WHERE");
        result.Sql.Should().Contain("ORDER BY");
        result.Sql.Should().Contain("FETCH NEXT 10 ROWS ONLY");
    }

    #endregion

    #region GetRestrictedSqlParameterized Tests

    [Fact]
    public void GetRestrictedSqlParameterized_RootQuery_GeneratesDistinctSelect()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var usersTable = dbModel.GetTableFromDbName("Users");

        var fromTable = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            GraphQlName = "Users"
        };

        var join = new TableJoin
        {
            Name = "orders",
            FromColumn = "Id",
            ConnectedColumn = "UserId",
            FromTable = fromTable
        };

        var queryLink = new QueryLink(join, fromTable, null);
        var parameters = new SqlParameterCollection();

        // Act
        var result = GqlObjectQuery.GetRestrictedSqlParameterized(dbModel, Dialect, parameters, queryLink);

        // Assert
        result.Sql.Should().Contain("SELECT DISTINCT");
        result.Sql.Should().Contain("[Id] AS [JoinId]");
        result.Sql.Should().Contain("FROM [Users]");
    }

    [Fact]
    public void GetRestrictedSqlParameterized_WithFilter_IncludesWhereClause()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var usersTable = dbModel.GetTableFromDbName("Users");

        var fromTable = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            GraphQlName = "Users",
            Filter = TableFilter.FromObject(new Dictionary<string, object?>
            {
                { "Name", new Dictionary<string, object?> { { "_contains", "test" } } }
            }, "Users")
        };

        var join = new TableJoin
        {
            Name = "orders",
            FromColumn = "Id",
            ConnectedColumn = "UserId",
            FromTable = fromTable
        };

        var queryLink = new QueryLink(join, fromTable, null);
        var parameters = new SqlParameterCollection();

        // Act
        var result = GqlObjectQuery.GetRestrictedSqlParameterized(dbModel, Dialect, parameters, queryLink);

        // Assert
        result.Sql.Should().Contain("WHERE");
        result.Sql.Should().Contain("LIKE");
    }

    [Fact]
    public void GetRestrictedSqlParameterized_NestedQuery_GeneratesNestedJoin()
    {
        // Arrange
        var dbModel = StandardTestFixtures.CompanyHierarchy();
        var companiesTable = dbModel.GetTableFromDbName("Companies");
        var departmentsTable = dbModel.GetTableFromDbName("Departments");

        var companyQuery = new GqlObjectQuery
        {
            DbTable = companiesTable,
            TableName = "Companies",
            GraphQlName = "Companies"
        };

        var departmentQuery = new GqlObjectQuery
        {
            DbTable = departmentsTable,
            TableName = "Departments",
            GraphQlName = "Departments"
        };

        var outerJoin = new TableJoin
        {
            Name = "departments",
            FromColumn = "Id",
            ConnectedColumn = "CompanyId",
            FromTable = companyQuery
        };

        var innerJoin = new TableJoin
        {
            Name = "employees",
            FromColumn = "Id",
            ConnectedColumn = "DepartmentId",
            Operator = "_eq",
            FromTable = departmentQuery
        };

        var parentLink = new QueryLink(outerJoin, companyQuery, null);
        var queryLink = new QueryLink(innerJoin, departmentQuery, parentLink);
        var parameters = new SqlParameterCollection();

        // Act
        var result = GqlObjectQuery.GetRestrictedSqlParameterized(dbModel, Dialect, parameters, queryLink);

        // Assert
        result.Sql.Should().Contain("INNER JOIN");
        result.Sql.Should().Contain("[Departments]");
    }

    #endregion

    #region SQL Generation with Joins

    [Fact]
    public void AddSqlParameterized_WithJoins_GeneratesJoinQueries()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var ordersTable = dbModel.GetTableFromDbName("Orders");

        var ordersLink = new GqlObjectQuery
        {
            DbTable = ordersTable,
            GraphQlName = "orders",
            TableName = "Orders",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Total") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            GraphQlName = "Users",
            Path = "Users",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") },
            Links = { ordersLink }
        };

        query.ConnectLinks(dbModel);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls.Should().ContainKey("Users");
        sqls.Should().ContainKey("Users->orders");
        sqls["Users->orders"].Sql.Should().Contain("INNER JOIN");
        sqls["Users->orders"].Sql.Should().Contain("[Orders]");
    }

    [Fact]
    public void AddSqlParameterized_NestedJoins_GeneratesAllJoinQueries()
    {
        // Arrange
        var dbModel = StandardTestFixtures.CompanyHierarchy();
        var companiesTable = dbModel.GetTableFromDbName("Companies");
        var departmentsTable = dbModel.GetTableFromDbName("Departments");
        var employeesTable = dbModel.GetTableFromDbName("Employees");

        var employeesLink = new GqlObjectQuery
        {
            DbTable = employeesTable,
            GraphQlName = "employees",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") }
        };

        var departmentsLink = new GqlObjectQuery
        {
            DbTable = departmentsTable,
            GraphQlName = "departments",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") },
            Links = { employeesLink }
        };

        var query = new GqlObjectQuery
        {
            DbTable = companiesTable,
            TableName = "Companies",
            GraphQlName = "Companies",
            Path = "Companies",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") },
            Links = { departmentsLink }
        };

        query.ConnectLinks(dbModel);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls.Should().ContainKey("Companies");
        sqls.Should().ContainKey("Companies->departments");
        sqls.Keys.Should().Contain(k => k.Contains("employees"));
    }

    #endregion

    #region JoinName Generation Tests

    [Fact]
    public void TableJoin_JoinName_GeneratesCorrectPath()
    {
        // Arrange
        var connectedQuery = GqlObjectQueryBuilder.Create()
            .WithTableName("Orders")
            .Build();

        var fromQuery = GqlObjectQueryBuilder.Create()
            .WithTableName("Users")
            .Build();
        fromQuery.Path = "users";

        var join = new TableJoin
        {
            Name = "orders",
            FromTable = fromQuery,
            ConnectedTable = connectedQuery
        };

        // Assert
        join.JoinName.Should().Be("users->orders");
    }

    [Fact]
    public void TableJoin_JoinName_WithAlias_UsesAlias()
    {
        // Arrange
        var connectedQuery = GqlObjectQueryBuilder.Create()
            .WithTableName("Orders")
            .Build();

        var fromQuery = GqlObjectQueryBuilder.Create()
            .WithTableName("Users")
            .Build();
        fromQuery.Path = "users";

        var join = new TableJoin
        {
            Name = "orders",
            Alias = "recentOrders",
            FromTable = fromQuery,
            ConnectedTable = connectedQuery
        };

        // Assert
        join.JoinName.Should().Be("users->recentOrders");
    }

    #endregion

    #region E-Commerce Integration Tests

    [Fact]
    public void ConnectLinks_ECommerceScenario_ConnectsAllRelationships()
    {
        // Arrange
        var dbModel = StandardTestFixtures.ECommerce();
        var ordersTable = dbModel.GetTableFromDbName("Orders");
        var orderItemsTable = dbModel.GetTableFromDbName("OrderItems");
        var productsTable = dbModel.GetTableFromDbName("Products");

        var productLink = new GqlObjectQuery
        {
            DbTable = productsTable,
            GraphQlName = "product",
            ScalarColumns = { new GqlObjectColumn("Name"), new GqlObjectColumn("Price") }
        };

        var itemsLink = new GqlObjectQuery
        {
            DbTable = orderItemsTable,
            GraphQlName = "items",
            ScalarColumns = { new GqlObjectColumn("Quantity"), new GqlObjectColumn("UnitPrice") },
            Links = { productLink }
        };

        var query = new GqlObjectQuery
        {
            DbTable = ordersTable,
            TableName = "Orders",
            GraphQlName = "Orders",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Total") },
            Links = { itemsLink }
        };

        // Act
        query.ConnectLinks(dbModel);

        // Assert
        query.Joins.Should().ContainSingle();
        var itemsJoin = query.Joins[0];
        itemsJoin.Name.Should().Be("items");
        itemsJoin.ConnectedTable.TableName.Should().Be("OrderItems");
        itemsJoin.ConnectedTable.Joins.Should().ContainSingle();
        itemsJoin.ConnectedTable.Joins[0].Name.Should().Be("product");
        itemsJoin.ConnectedTable.Joins[0].QueryType.Should().Be(QueryType.Single);
    }

    [Fact]
    public void AddSqlParameterized_ECommerceWithFilters_GeneratesComplexQuery()
    {
        // Arrange
        var dbModel = StandardTestFixtures.ECommerce();
        var ordersTable = dbModel.GetTableFromDbName("Orders");
        var orderItemsTable = dbModel.GetTableFromDbName("OrderItems");

        var itemsLink = new GqlObjectQuery
        {
            DbTable = orderItemsTable,
            GraphQlName = "items",
            TableName = "OrderItems",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Quantity") },
            Filter = TableFilter.FromObject(new Dictionary<string, object?>
            {
                { "Quantity", new Dictionary<string, object?> { { "_gt", 1 } } }
            }, "OrderItems")
        };

        var query = new GqlObjectQuery
        {
            DbTable = ordersTable,
            TableName = "Orders",
            GraphQlName = "Orders",
            Path = "Orders",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Total") },
            Filter = TableFilter.FromObject(new Dictionary<string, object?>
            {
                { "Status", new Dictionary<string, object?> { { "_eq", "completed" } } }
            }, "Orders"),
            Links = { itemsLink }
        };

        query.ConnectLinks(dbModel);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls["Orders"].Sql.Should().Contain("WHERE");
        sqls["Orders"].Sql.Should().Contain("@p0");

        sqls.Should().ContainKey("Orders->items");
        sqls["Orders->items"].Sql.Should().Contain("WHERE");
    }

    #endregion
}
