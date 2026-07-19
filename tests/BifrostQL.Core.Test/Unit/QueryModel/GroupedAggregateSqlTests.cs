using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.QueryModel;

/// <summary>
/// Structural validity of the GROUP BY aggregate SQL across every dialect grammar
/// (ScriptDom for SQL Server; SqlParserCS for Postgres/MySQL/SQLite), plus the
/// no-user-text-concatenated invariant: the filter value must travel as a bound
/// parameter, never inlined into the statement.
/// </summary>
public sealed class GroupedAggregateSqlTests
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
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Region", "nvarchar")
                .WithColumn("TenantId", "int")
                .WithColumn("Amount", "decimal"))
            .Build();

    private static GqlObjectQuery BuildGroupedQuery(IDbModel model, TableFilter? filter = null)
    {
        var orders = model.GetTableFromDbName("Orders");
        var region = orders.Columns.Single(c => c.DbName == "Region");
        var amount = orders.Columns.Single(c => c.DbName == "Amount");

        var valueColumns = new List<AggregateValueColumn>();
        foreach (var (opGroup, operation) in AggregateSurface.ValueOps)
            valueColumns.Add(new AggregateValueColumn(operation, amount, opGroup, AggregateSurface.ValueAlias(opGroup, amount.GraphQlName)));

        return new GqlObjectQuery
        {
            DbTable = orders,
            TableName = "Orders",
            GraphQlName = "Orders",
            Path = "Orders",
            Filter = filter,
            GroupedAggregate = new GroupedAggregate
            {
                GroupColumns = new[] { new AggregateGroupColumn(region, region.GraphQlName) },
                IncludeCount = true,
                ValueColumns = valueColumns,
            },
        };
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void GroupedAggregate_ParsesForDialect(ISqlDialect dialect, SqlFlavor flavor)
    {
        var model = BuildModel();
        var query = BuildGroupedQuery(model);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, dialect, sqls, parameters);

        sqls.Should().ContainKey("Orders");
        var sql = sqls["Orders"].Sql;
        SqlSyntax.AssertValid(sql, flavor, "grouped aggregate SQL");
        sql.Should().Contain("GROUP BY");
        sql.Should().Contain("COUNT(*)");
        sql.Should().Contain("SUM(");
        sql.Should().Contain("AVG(");
        sql.Should().Contain("MIN(");
        sql.Should().Contain("MAX(");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void GroupedAggregate_WithFilter_ParametersNotInlined(ISqlDialect dialect, SqlFlavor flavor)
    {
        var model = BuildModel();
        var filter = TableFilterFactory.Equals("Orders", "TenantId", 5);
        var query = BuildGroupedQuery(model, filter);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, dialect, sqls, parameters);

        var built = sqls["Orders"];
        SqlSyntax.AssertValid(built.Sql, flavor, "filtered grouped aggregate SQL");
        built.Sql.Should().Contain("WHERE");
        built.Sql.Should().Contain("GROUP BY");
        // The tenant value travels as a bound parameter, never concatenated in.
        built.Parameters.Should().ContainSingle(p => Equals(p.Value, 5));
        built.Sql.Should().NotContain(" 5");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void GroupedAggregate_NoGroupColumns_OmitsGroupByClause(ISqlDialect dialect, SqlFlavor flavor)
    {
        var model = BuildModel();
        var orders = model.GetTableFromDbName("Orders");
        var amount = orders.Columns.Single(c => c.DbName == "Amount");

        var query = new GqlObjectQuery
        {
            DbTable = orders,
            TableName = "Orders",
            GraphQlName = "Orders",
            Path = "Orders",
            GroupedAggregate = new GroupedAggregate
            {
                GroupColumns = Array.Empty<AggregateGroupColumn>(),
                IncludeCount = true,
                ValueColumns = new[]
                {
                    new AggregateValueColumn(AggregateOperationType.Sum, amount, "_sum", AggregateSurface.ValueAlias("_sum", amount.GraphQlName)),
                },
            },
        };

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, dialect, sqls, parameters);

        var sql = sqls["Orders"].Sql;
        SqlSyntax.AssertValid(sql, flavor, "whole-table aggregate SQL");
        sql.Should().NotContain("GROUP BY");
        sql.Should().Contain("COUNT(*)");
    }
}
