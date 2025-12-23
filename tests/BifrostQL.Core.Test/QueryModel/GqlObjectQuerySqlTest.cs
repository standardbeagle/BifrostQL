using FluentAssertions;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Comprehensive tests for GqlObjectQuery SQL generation.
/// Tests cover parameterized SQL generation, joins, filters, pagination, and sorting.
/// </summary>
public sealed class GqlObjectQuerySqlTest
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    #region Basic Select Tests

    [Fact]
    public void AddSqlParameterized_BasicSelect_GeneratesCorrectSql()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id", "Name", "Email")
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls.Should().ContainSingle();
        var sql = sqls["Users"];
        sql.Sql.Should().Contain("SELECT");
        sql.Sql.Should().Contain("[Id]");
        sql.Sql.Should().Contain("[Name]");
        sql.Sql.Should().Contain("[Email]");
        sql.Sql.Should().Contain("FROM [Users]");
    }

    [Fact]
    public void AddSqlParameterized_WithAlias_UsesAliasAsKey()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithAlias("myUsers")
            .WithColumns("Id")
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls.Should().ContainKey("myUsers");
    }

    [Fact]
    public void AddSqlParameterized_WithSchema_IncludesSchemaInTableReference()
    {
        // Arrange
        var dbModel = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name"))
            .Build();

        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithSchema("dbo")
            .WithColumns("Id", "Name")
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls["Users"].Sql.Should().Contain("[dbo].[Users]");
    }

    #endregion

    #region Filter Tests

    [Fact]
    public void AddSqlParameterized_WithSimpleFilter_GeneratesWhereClause()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Id", new Dictionary<string, object?> { { "_eq", 42 } } }
        }, "Users");

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id", "Name")
            .WithFilter(filter)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        var sql = sqls["Users"];
        sql.Sql.Should().Contain("WHERE");
        sql.Sql.Should().Contain("[Id] = @p0");
        sql.Parameters.Should().ContainSingle(p => p.Name == "@p0" && (int)p.Value! == 42);
    }

    [Fact]
    public void AddSqlParameterized_WithAndFilter_GeneratesMultipleConditions()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "and", new object[]
                {
                    new Dictionary<string, object?> { { "Name", new Dictionary<string, object?> { { "_eq", "John" } } } },
                    new Dictionary<string, object?> { { "Email", new Dictionary<string, object?> { { "_contains", "@test.com" } } } }
                }
            }
        }, "Users");

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithFilter(filter)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        var sql = sqls["Users"];
        sql.Sql.Should().Contain("WHERE");
        sql.Sql.Should().Contain("AND");
        sql.Parameters.Count.Should().Be(2);
    }

    [Fact]
    public void AddSqlParameterized_WithOrFilter_GeneratesOrConditions()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "or", new object[]
                {
                    new Dictionary<string, object?> { { "Name", new Dictionary<string, object?> { { "_eq", "John" } } } },
                    new Dictionary<string, object?> { { "Name", new Dictionary<string, object?> { { "_eq", "Jane" } } } }
                }
            }
        }, "Users");

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithFilter(filter)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        var sql = sqls["Users"];
        sql.Sql.Should().Contain("WHERE");
        sql.Sql.Should().Contain("OR");
        sql.Parameters.Count.Should().Be(2);
    }

    [Fact]
    public void AddSqlParameterized_WithNullFilter_OmitsWhereClause()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls["Users"].Sql.Should().NotContain("WHERE");
    }

    [Fact]
    public void AddSqlParameterized_WithInFilter_GeneratesInClause()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Id", new Dictionary<string, object?> { { "_in", new object[] { 1, 2, 3, 4, 5 } } } }
        }, "Users");

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithFilter(filter)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        var sql = sqls["Users"];
        sql.Sql.Should().Contain("IN (");
        sql.Parameters.Count.Should().Be(5);
    }

    [Fact]
    public void AddSqlParameterized_WithBetweenFilter_GeneratesBetweenClause()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Id", new Dictionary<string, object?> { { "_between", new object[] { 10, 20 } } } }
        }, "Users");

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithFilter(filter)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        var sql = sqls["Users"];
        sql.Sql.Should().Contain("BETWEEN");
        sql.Parameters.Count.Should().Be(2);
    }

    #endregion

    #region Pagination Tests

    [Fact]
    public void AddSqlParameterized_WithLimit_GeneratesTopClause()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithLimit(10)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls["Users"].Sql.Should().Contain("FETCH NEXT 10 ROWS ONLY");
    }

    [Fact]
    public void AddSqlParameterized_WithOffset_GeneratesOffsetClause()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithOffset(5)
            .WithLimit(10)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        var sql = sqls["Users"].Sql;
        sql.Should().Contain("OFFSET 5 ROWS");
        sql.Should().Contain("FETCH NEXT 10 ROWS ONLY");
    }

    [Fact]
    public void AddSqlParameterized_WithPagination_GeneratesCorrectSql()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithPagination(20, 10)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        var sql = sqls["Users"].Sql;
        sql.Should().Contain("OFFSET 20 ROWS");
        sql.Should().Contain("FETCH NEXT 10 ROWS ONLY");
    }

    #endregion

    #region Sorting Tests

    [Fact]
    public void AddSqlParameterized_WithAscSort_GeneratesOrderByAsc()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id", "Name")
            .WithSort("Name_asc")
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls["Users"].Sql.Should().Contain("ORDER BY Name asc");
    }

    [Fact]
    public void AddSqlParameterized_WithDescSort_GeneratesOrderByDesc()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id", "Name")
            .WithSort("Name_desc")
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls["Users"].Sql.Should().Contain("ORDER BY Name desc");
    }

    [Fact]
    public void AddSqlParameterized_WithMultipleSortColumns_GeneratesMultiColumnOrderBy()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id", "Name", "Email")
            .WithSort("Name_asc", "Email_desc")
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        var sql = sqls["Users"].Sql;
        sql.Should().Contain("ORDER BY Name asc, Email desc");
    }

    [Fact]
    public void AddSqlParameterized_WithInvalidSortSuffix_ThrowsNotSupportedException()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithSort("Name_invalid")
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act & Assert
        Action act = () => query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);
        act.Should().Throw<NotSupportedException>();
    }

    #endregion

    #region IncludeResult (Count) Tests

    [Fact]
    public void AddSqlParameterized_WithIncludeResult_GeneratesCountQuery()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .IncludeResult()
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls.Should().ContainKey("Users=>count");
        sqls["Users=>count"].Sql.Should().StartWith("SELECT COUNT(*) FROM");
    }

    [Fact]
    public void AddSqlParameterized_WithIncludeResultAndFilter_CountIncludesFilter()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_contains", "test" } } }
        }, "Users");

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithFilter(filter)
            .IncludeResult()
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        var countSql = sqls["Users=>count"];
        countSql.Sql.Should().Contain("WHERE");
        countSql.Sql.Should().Contain("LIKE");
    }

    #endregion

    #region GetFilterSqlParameterized Tests

    [Fact]
    public void GetFilterSqlParameterized_WithNullFilter_ReturnsEmpty()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .Build();

        var parameters = new SqlParameterCollection();

        // Act
        var result = query.GetFilterSqlParameterized(dbModel, Dialect, parameters);

        // Assert
        result.Should().Be(ParameterizedSql.Empty);
    }

    [Fact]
    public void GetFilterSqlParameterized_WithFilter_StartsWithWhere()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Id", new Dictionary<string, object?> { { "_eq", 1 } } }
        }, "Users");

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithFilter(filter)
            .Build();

        var parameters = new SqlParameterCollection();

        // Act
        var result = query.GetFilterSqlParameterized(dbModel, Dialect, parameters);

        // Assert
        result.Sql.Should().StartWith(" WHERE ");
    }

    [Fact]
    public void GetFilterSqlParameterized_WithAlias_UsesAliasInColumnReference()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Id", new Dictionary<string, object?> { { "_eq", 1 } } }
        }, "Users");

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithFilter(filter)
            .Build();

        var parameters = new SqlParameterCollection();

        // Act
        var result = query.GetFilterSqlParameterized(dbModel, Dialect, parameters, "u");

        // Assert
        result.Sql.Should().Contain("[u].[Id]");
    }

    #endregion

    #region Aggregate Column Tests

    [Fact]
    public void AddSqlParameterized_WithAggregateColumn_GeneratesAggregateQuery()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var usersTable = dbModel.GetTableFromDbName("Users");

        // Get the link between Users and Orders
        var link = usersTable.MultiLinks["orders"];
        var links = new List<(LinkDirection, TableLinkDto)> { (LinkDirection.OneToMany, link) };

        var aggregateColumn = new GqlAggregateColumn(
            links,
            "Total",
            "totalOrderAmount",
            AggregateOperationType.Sum);

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithAggregateColumn(aggregateColumn)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls.Should().ContainKey("Users=>agg_totalOrderAmount");
    }

    #endregion

    #region FullColumnNames Tests

    [Fact]
    public void FullColumnNames_ExcludesMetaColumns()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumn("Id")
            .WithColumn("__typename")
            .WithColumn("Name")
            .Build();

        // Act
        var columns = query.FullColumnNames.ToList();

        // Assert
        columns.Select(c => c.DbDbName).Should().NotContain("__typename");
        columns.Select(c => c.DbDbName).Should().Contain("Id");
        columns.Select(c => c.DbDbName).Should().Contain("Name");
    }

    [Fact]
    public void FullColumnNames_IncludesJoinFromColumns()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var ordersTable = dbModel.GetTableFromDbName("Orders");
        var usersTable = dbModel.GetTableFromDbName("Users");

        var userQuery = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id", "Name")
            .Build();

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(ordersTable)
            .WithColumns("Id", "Total")
            .WithJoin(j => j
                .WithName("user")
                .WithFromColumn("UserId")
                .WithConnectedColumn("Id")
                .WithFromTable(new GqlObjectQuery { TableName = "Orders" })
                .WithConnectedTable(userQuery))
            .Build();

        // Act
        var columns = query.FullColumnNames.ToList();

        // Assert
        columns.Select(c => c.DbDbName).Should().Contain("UserId");
    }

    [Fact]
    public void FullColumnNames_DeduplicatesColumns()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumn("Id")
            .WithColumn("Id")
            .WithColumn("Name")
            .Build();

        // Act
        var columns = query.FullColumnNames.ToList();

        // Assert
        columns.Count(c => c.DbDbName == "Id").Should().Be(1);
    }

    #endregion

    #region KeyName Tests

    [Fact]
    public void KeyName_WithoutAlias_ReturnsGraphQlName()
    {
        // Arrange
        var query = GqlObjectQueryBuilder.Create()
            .WithGraphQlName("users")
            .Build();

        // Act & Assert
        query.KeyName.Should().Be("users");
    }

    [Fact]
    public void KeyName_WithAlias_ReturnsAlias()
    {
        // Arrange
        var query = GqlObjectQueryBuilder.Create()
            .WithGraphQlName("users")
            .WithAlias("allUsers")
            .Build();

        // Act & Assert
        query.KeyName.Should().Be("allUsers");
    }

    #endregion

    #region RecurseJoins Tests

    [Fact]
    public void RecurseJoins_WithNoJoins_ReturnsEmpty()
    {
        // Arrange
        var query = GqlObjectQueryBuilder.Create().Build();

        // Act & Assert
        query.RecurseJoins.Should().BeEmpty();
    }

    [Fact]
    public void RecurseJoins_WithNestedJoins_ReturnsAllJoins()
    {
        // Arrange
        var deepestQuery = GqlObjectQueryBuilder.Create()
            .WithTableName("C")
            .Build();

        var middleQuery = GqlObjectQueryBuilder.Create()
            .WithTableName("B")
            .WithJoin(j => j
                .WithName("toC")
                .WithFromColumn("CId")
                .WithConnectedColumn("Id")
                .WithFromTable(new GqlObjectQuery { TableName = "B" })
                .WithConnectedTable(deepestQuery))
            .Build();

        var query = GqlObjectQueryBuilder.Create()
            .WithTableName("A")
            .WithJoin(j => j
                .WithName("toB")
                .WithFromColumn("BId")
                .WithConnectedColumn("Id")
                .WithFromTable(new GqlObjectQuery { TableName = "A" })
                .WithConnectedTable(middleQuery))
            .Build();

        // Act
        var allJoins = query.RecurseJoins.ToList();

        // Assert
        allJoins.Should().HaveCount(2);
        allJoins.Select(j => j.Name).Should().Contain("toB");
        allJoins.Select(j => j.Name).Should().Contain("toC");
    }

    #endregion

    #region GetJoin Tests

    [Fact]
    public void GetJoin_ByName_ReturnsMatchingJoin()
    {
        // Arrange
        var connectedQuery = GqlObjectQueryBuilder.Create()
            .WithTableName("B")
            .Build();

        var query = GqlObjectQueryBuilder.Create()
            .WithTableName("A")
            .WithJoin(j => j
                .WithName("myJoin")
                .WithFromColumn("BId")
                .WithConnectedColumn("Id")
                .WithFromTable(new GqlObjectQuery { TableName = "A" })
                .WithConnectedTable(connectedQuery))
            .Build();

        // Act
        var join = query.GetJoin(null, "myJoin");

        // Assert
        join.Should().NotBeNull();
        join!.Name.Should().Be("myJoin");
    }

    [Fact]
    public void GetJoin_ByAlias_ReturnsMatchingJoin()
    {
        // Arrange
        var connectedQuery = GqlObjectQueryBuilder.Create()
            .WithTableName("B")
            .Build();

        var query = GqlObjectQueryBuilder.Create()
            .WithTableName("A")
            .WithJoin(j => j
                .WithName("myJoin")
                .WithAlias("joinAlias")
                .WithFromColumn("BId")
                .WithConnectedColumn("Id")
                .WithFromTable(new GqlObjectQuery { TableName = "A" })
                .WithConnectedTable(connectedQuery))
            .Build();

        // Act
        var join = query.GetJoin("joinAlias", "differentName");

        // Assert
        join.Should().NotBeNull();
        join!.Alias.Should().Be("joinAlias");
    }

    [Fact]
    public void GetJoin_NotFound_ReturnsNull()
    {
        // Arrange
        var query = GqlObjectQueryBuilder.Create()
            .WithTableName("A")
            .Build();

        // Act
        var join = query.GetJoin(null, "nonExistent");

        // Assert
        join.Should().BeNull();
    }

    #endregion

    #region GetAggregate Tests

    [Fact]
    public void GetAggregate_ByName_ReturnsMatchingAggregate()
    {
        // Arrange
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var link = usersTable.MultiLinks["orders"];
        var links = new List<(LinkDirection, TableLinkDto)> { (LinkDirection.OneToMany, link) };

        var aggregate = new GqlAggregateColumn(
            links,
            "Id",
            "userCount",
            AggregateOperationType.Count);

        var query = GqlObjectQueryBuilder.Create()
            .WithTableName("Users")
            .WithAggregateColumn(aggregate)
            .Build();

        // Act
        var result = query.GetAggregate(null, "userCount");

        // Assert
        result.Should().NotBeNull();
        result!.FinalColumnGraphQlName.Should().Be("userCount");
    }

    [Fact]
    public void GetAggregate_NotFound_ReturnsNull()
    {
        // Arrange
        var query = GqlObjectQueryBuilder.Create()
            .WithTableName("Users")
            .Build();

        // Act
        var result = query.GetAggregate(null, "nonExistent");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Integration Tests - Complex Queries

    [Fact]
    public void AddSqlParameterized_ComplexQuery_GeneratesAllComponents()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_contains", "test" } } }
        }, "Users");

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id", "Name", "Email")
            .WithFilter(filter)
            .WithSort("Name_asc", "Email_desc")
            .WithPagination(10, 20)
            .IncludeResult()
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        var sql = sqls["Users"];
        sql.Sql.Should().Contain("SELECT");
        sql.Sql.Should().Contain("[Id]");
        sql.Sql.Should().Contain("[Name]");
        sql.Sql.Should().Contain("[Email]");
        sql.Sql.Should().Contain("FROM [Users]");
        sql.Sql.Should().Contain("WHERE");
        sql.Sql.Should().Contain("LIKE");
        sql.Sql.Should().Contain("ORDER BY");
        sql.Sql.Should().Contain("OFFSET 10 ROWS");
        sql.Sql.Should().Contain("FETCH NEXT 20 ROWS ONLY");

        sqls.Should().ContainKey("Users=>count");
    }

    [Fact]
    public void AddSqlParameterized_ECommerceQuery_GeneratesValidSql()
    {
        // Arrange
        var dbModel = StandardTestFixtures.ECommerce();
        var productsTable = dbModel.GetTableFromDbName("Products");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "and", new object[]
                {
                    new Dictionary<string, object?> { { "Price", new Dictionary<string, object?> { { "_gte", 10.0 } } } },
                    new Dictionary<string, object?> { { "Stock", new Dictionary<string, object?> { { "_gt", 0 } } } }
                }
            }
        }, "Products");

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(productsTable)
            .WithColumns("Id", "Name", "Price", "Stock")
            .WithFilter(filter)
            .WithSort("Price_asc")
            .WithLimit(50)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        var sql = sqls["Products"];
        sql.Sql.Should().Contain("FROM [Products]");
        sql.Sql.Should().Contain("WHERE");
        sql.Sql.Should().Contain(">= @p0");
        sql.Sql.Should().Contain("> @p1");
        sql.Sql.Should().Contain("ORDER BY");
        sql.Sql.Should().Contain("FETCH NEXT 50 ROWS ONLY");
        sql.Parameters.Count.Should().Be(2);
    }

    #endregion

    #region Parameter Isolation Tests

    [Fact]
    public void AddSqlParameterized_MultipleQueries_ParametersAreIsolated()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");

        var filter1 = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Id", new Dictionary<string, object?> { { "_eq", 1 } } }
        }, "Users");

        var filter2 = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Id", new Dictionary<string, object?> { { "_eq", 2 } } }
        }, "Users");

        var query1 = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithGraphQlName("query1")
            .WithColumns("Id")
            .WithFilter(filter1)
            .Build();

        var query2 = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithGraphQlName("query2")
            .WithColumns("Id")
            .WithFilter(filter2)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query1.AddSqlParameterized(dbModel, Dialect, sqls, parameters);
        query2.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        parameters.Parameters.Should().HaveCount(2);
        parameters.Parameters[0].Name.Should().Be("@p0");
        parameters.Parameters[1].Name.Should().Be("@p1");
        parameters.Parameters[0].Value.Should().Be(1);
        parameters.Parameters[1].Value.Should().Be(2);
    }

    #endregion

    #region ToString Tests

    [Fact]
    public void ToString_ReturnsTableName()
    {
        // Arrange
        var query = GqlObjectQueryBuilder.Create()
            .WithTableName("MyTable")
            .Build();

        // Act & Assert
        query.ToString().Should().Be("MyTable");
    }

    #endregion
}
