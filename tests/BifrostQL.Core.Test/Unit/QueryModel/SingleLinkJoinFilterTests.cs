using FluentAssertions;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Model;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Regression tests for the single-link (QueryType.Single) FK-navigation
/// security hole: the connected table's filter (tenant scope, soft-delete,
/// policy row filters injected by QueryTransformerService into
/// ConnectedTable.Filter) was dropped for single-link navigation, so
/// `orders { customer_single { ... } }` could return a soft-deleted /
/// other-tenant / policy-excluded parent row. The fix renders that filter as a
/// WHERE on alias "b", mirroring the multi-link path.
/// </summary>
public sealed class SingleLinkJoinFilterTests
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    [Fact]
    public void ToConnectedSqlParameterized_SingleQueryType_AppliesConnectedTableFilter()
    {
        // Arrange: a single-link (many-to-one) navigation Orders -> user, where
        // the connected Users table carries a filter that stands in for an
        // injected tenant / soft-delete / policy row-scope predicate.
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var usersTable = dbModel.GetTableFromDbName("Users");

        var connectedQuery = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") },
            Filter = TableFilter.FromObject(new Dictionary<string, object?>
            {
                { "Name", new Dictionary<string, object?> { { "_eq", "scoped" } } }
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

        // Assert: the injected filter is now rendered as a WHERE on alias "b"
        // (the connected table), and its parameter is threaded through so the
        // scope actually binds at execution time.
        result.Sql.Should().Contain("WHERE");
        result.Sql.Should().Contain("[b].[Name]");
        result.Parameters.Should().NotBeEmpty();
    }

    [Fact]
    public void ToConnectedSqlParameterized_SingleQueryType_NoFilter_EmitsNoWhere()
    {
        // Arrange: single-link navigation with no connected-table filter must
        // stay clean — the fix must not synthesize a spurious WHERE.
        var dbModel = StandardTestFixtures.UsersWithOrders();
        var usersTable = dbModel.GetTableFromDbName("Users");

        var connectedQuery = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") }
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
        result.Sql.Should().Contain("INNER JOIN");
        result.Sql.Should().NotContain("WHERE");
    }
}
