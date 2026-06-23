using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Test.TestSupport;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.QueryModel;

/// <summary>
/// Parses the actual SQL emitted by the query builder against each engine's real
/// grammar (ScriptDom for SQL Server, SqlParserCS for Postgres/MySQL/SQLite).
/// Catches structural defects — stray commas, empty projections — in the generated
/// output that string assertions miss, across the SELECT / join / paged-collection
/// shapes and every dialect.
/// </summary>
public sealed class GeneratedSqlValidityTests
{
    public static IEnumerable<object[]> Dialects => new[]
    {
        new object[] { SqlServerDialect.Instance, SqlFlavor.SqlServer },
        new object[] { PostgresDialect.Instance, SqlFlavor.Postgres },
        new object[] { MySqlDialect.Instance, SqlFlavor.MySql },
        new object[] { SqliteDialect.Instance, SqlFlavor.Sqlite },
    };

    private static IDbModel BuildModel()
        => DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("Total", "decimal"))
            .WithMultiLink("Users", "Id", "Orders", "UserId", "Orders")
            .Build();

    [Theory]
    [MemberData(nameof(Dialects))]
    public void TopLevelSelectWithPagedChild_ParsesForDialect(ISqlDialect dialect, SqlFlavor flavor)
    {
        var model = BuildModel();
        var users = model.GetTableFromDbName("Users");

        var ordersLink = new GqlObjectQuery
        {
            GraphQlName = "Orders",
            IncludeResult = true,
            Limit = 10,
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Total") },
        };
        var query = new GqlObjectQuery
        {
            DbTable = users,
            TableName = "Users",
            GraphQlName = "Users",
            Path = "Users",
            IncludeResult = true,
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") },
            Links = { ordersLink },
        };
        query.ConnectLinks(model);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, dialect, sqls, parameters);

        Assert.NotEmpty(sqls);
        foreach (var (key, sql) in sqls)
            SqlSyntax.AssertValid(sql.Sql, flavor, $"generated SQL for '{key}'");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void TopLevelSelectNoChildren_ParsesForDialect(ISqlDialect dialect, SqlFlavor flavor)
    {
        var model = BuildModel();
        var users = model.GetTableFromDbName("Users");

        var query = new GqlObjectQuery
        {
            DbTable = users,
            TableName = "Users",
            GraphQlName = "Users",
            Path = "Users",
            IncludeResult = true,
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") },
        };
        query.ConnectLinks(model);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, dialect, sqls, parameters);

        Assert.NotEmpty(sqls);
        foreach (var (key, sql) in sqls)
            SqlSyntax.AssertValid(sql.Sql, flavor, $"generated SQL for '{key}'");
    }
}
