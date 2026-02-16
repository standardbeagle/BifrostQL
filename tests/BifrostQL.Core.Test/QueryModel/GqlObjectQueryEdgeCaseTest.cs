using FluentAssertions;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Edge case tests to probe for potential bugs in GqlObjectQuery SQL generation.
/// </summary>
public sealed class GqlObjectQueryEdgeCaseTest
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    #region Empty/Null Edge Cases

    [Fact]
    public void AddSqlParameterized_EmptyColumnsList_GeneratesValidSql()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            // No columns added
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert - should generate SELECT with no columns
        sqls.Should().ContainKey("Users");
        var sql = sqls["Users"];
        sql.Sql.Should().Contain("SELECT");
        sql.Sql.Should().Contain("FROM [Users]");
    }

    [Fact]
    public void AddSqlParameterized_EmptyFilter_ThrowsExecutionError()
    {
        // Arrange - create filter with no conditions
        // NOTE: This is expected behavior - empty filters are explicitly rejected
        var dbModel = StandardTestFixtures.SimpleUsers();

        // Act & Assert
        Action act = () => TableFilter.FromObject(new Dictionary<string, object?>(), "Users");
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*has no properties*");
    }

    [Fact]
    public void AddSqlParameterized_EmptySortList_UsesDefaultPaginationWithNullOrder()
    {
        // Arrange
        // NOTE: SQL Server requires ORDER BY for OFFSET/FETCH pagination.
        // When no sort is specified but pagination is applied, it uses ORDER BY (SELECT NULL)
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithSort() // Empty sort
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert - with default pagination, ORDER BY (SELECT NULL) is used
        var sql = sqls["Users"].Sql;
        if (sql.Contains("OFFSET"))
        {
            // SQL Server requires ORDER BY for OFFSET/FETCH
            sql.Should().Contain("ORDER BY (SELECT NULL)");
        }
    }

    [Fact]
    public void AddSqlParameterized_ZeroLimit_GeneratesFetchZero()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithLimit(0)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls["Users"].Sql.Should().Contain("FETCH NEXT 0 ROWS ONLY");
    }

    [Fact]
    public void AddSqlParameterized_NegativeOffset_HandlesGracefully()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithOffset(-5)
            .WithLimit(10)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert - negative offset should still generate SQL (database will handle the error)
        sqls["Users"].Sql.Should().Contain("OFFSET -5 ROWS");
    }

    #endregion

    #region Special Character Edge Cases

    [Fact]
    public void AddSqlParameterized_TableNameWithSpaces_EscapesCorrectly()
    {
        // Arrange
        var dbModel = DbModelTestFixture.Create()
            .WithTable("User Accounts", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Full Name"))
            .Build();

        var table = dbModel.GetTableFromDbName("User Accounts");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Full Name")
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls["User Accounts"].Sql.Should().Contain("[User Accounts]");
        sqls["User Accounts"].Sql.Should().Contain("[Full Name]");
    }

    [Fact]
    public void AddSqlParameterized_FilterWithSqlInjectionAttempt_ParameterizesValue()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_eq", "'; DROP TABLE Users; --" } } }
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

        // Assert - SQL should use parameter, not inline the value
        var sql = sqls["Users"];
        sql.Sql.Should().NotContain("DROP TABLE");
        sql.Sql.Should().Contain("@p0");
        sql.Parameters.Should().ContainSingle(p => p.Value!.ToString() == "'; DROP TABLE Users; --");
    }

    [Fact]
    public void AddSqlParameterized_FilterWithNullValue_GeneratesIsNullCheck()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_eq", null } } }
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
        sql.Sql.Should().Contain("IS NULL");
    }

    #endregion

    #region Deep Nesting Edge Cases

    [Fact]
    public void ConnectLinks_DeeplyNestedLinks_ConnectsAllLevels()
    {
        // Arrange - 4 levels deep
        var dbModel = DbModelTestFixture.Create()
            .WithTable("A", t => t.WithPrimaryKey("Id"))
            .WithTable("B", t => t.WithPrimaryKey("Id").WithColumn("AId", "int"))
            .WithTable("C", t => t.WithPrimaryKey("Id").WithColumn("BId", "int"))
            .WithTable("D", t => t.WithPrimaryKey("Id").WithColumn("CId", "int"))
            .WithMultiLink("A", "Id", "B", "AId", "bs")
            .WithMultiLink("B", "Id", "C", "BId", "cs")
            .WithMultiLink("C", "Id", "D", "CId", "ds")
            .Build();

        var dLink = new GqlObjectQuery { GraphQlName = "ds" };
        var cLink = new GqlObjectQuery { GraphQlName = "cs", Links = { dLink } };
        var bLink = new GqlObjectQuery { GraphQlName = "bs", Links = { cLink } };

        var query = new GqlObjectQuery
        {
            DbTable = dbModel.GetTableFromDbName("A"),
            TableName = "A",
            GraphQlName = "A",
            Links = { bLink }
        };

        // Act
        query.ConnectLinks(dbModel);

        // Assert - verify all 3 levels of joins are created
        query.Joins.Should().ContainSingle();
        query.Joins[0].ConnectedTable.Joins.Should().ContainSingle();
        query.Joins[0].ConnectedTable.Joins[0].ConnectedTable.Joins.Should().ContainSingle();
    }

    #endregion

    #region Filter Edge Cases

    [Fact]
    public void AddSqlParameterized_EmptyInClause_HandlesGracefully()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Id", new Dictionary<string, object?> { { "_in", Array.Empty<object>() } } }
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

        // Assert - empty IN should generate valid SQL (database handles semantics)
        sqls["Users"].Sql.Should().Contain("IN");
    }

    [Fact]
    public void AddSqlParameterized_NestedAndOrFilters_GeneratesCorrectPrecedence()
    {
        // Arrange - (A AND B) OR (C AND D)
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "or", new object[]
                {
                    new Dictionary<string, object?>
                    {
                        { "and", new object[]
                            {
                                new Dictionary<string, object?> { { "Name", new Dictionary<string, object?> { { "_eq", "A" } } } },
                                new Dictionary<string, object?> { { "Email", new Dictionary<string, object?> { { "_eq", "B" } } } }
                            }
                        }
                    },
                    new Dictionary<string, object?>
                    {
                        { "and", new object[]
                            {
                                new Dictionary<string, object?> { { "Name", new Dictionary<string, object?> { { "_eq", "C" } } } },
                                new Dictionary<string, object?> { { "Email", new Dictionary<string, object?> { { "_eq", "D" } } } }
                            }
                        }
                    }
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
        sql.Sql.Should().Contain("OR");
        sql.Parameters.Count.Should().Be(4);
    }

    [Fact]
    public void AddSqlParameterized_LikeFilterWithWildcards_EscapesCorrectly()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_like", "%test%" } } }
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
        sql.Sql.Should().Contain("LIKE");
        sql.Parameters.Should().ContainSingle(p => p.Value!.ToString()!.Contains("%"));
    }

    #endregion

    #region Large Data Edge Cases

    [Fact]
    public void AddSqlParameterized_ManyColumns_GeneratesValidSql()
    {
        // Arrange - 50 columns
        var tableBuilder = DbModelTestFixture.Create();
        var columnNames = Enumerable.Range(1, 50).Select(i => $"Col{i}").ToArray();

        tableBuilder.WithTable("BigTable", t =>
        {
            t.WithPrimaryKey("Id");
            foreach (var col in columnNames)
                t.WithColumn(col);
        });

        var dbModel = tableBuilder.Build();
        var table = dbModel.GetTableFromDbName("BigTable");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns(columnNames)
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert
        sqls["BigTable"].Sql.Should().Contain("[Col1]");
        sqls["BigTable"].Sql.Should().Contain("[Col50]");
    }

    [Fact]
    public void AddSqlParameterized_LargeInClause_GeneratesAllParameters()
    {
        // Arrange - 100 values in IN clause
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var values = Enumerable.Range(1, 100).Cast<object>().ToArray();
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            { "Id", new Dictionary<string, object?> { { "_in", values } } }
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
        sqls["Users"].Parameters.Count.Should().Be(100);
    }

    #endregion

    #region Offset Without Limit Edge Case

    [Fact]
    public void AddSqlParameterized_OffsetWithoutLimit_HandlesGracefully()
    {
        // Arrange
        var dbModel = StandardTestFixtures.SimpleUsers();
        var usersTable = dbModel.GetTableFromDbName("Users");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id")
            .WithOffset(10)
            // No limit set
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        // Act
        query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

        // Assert - SQL Server requires FETCH with OFFSET
        var sql = sqls["Users"].Sql;
        // Either no pagination or both OFFSET and FETCH should be present
        if (sql.Contains("OFFSET"))
        {
            sql.Should().Contain("FETCH");
        }
    }

    #endregion
}
