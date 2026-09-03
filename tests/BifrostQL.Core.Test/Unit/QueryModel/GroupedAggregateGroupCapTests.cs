using BifrostQL.Core.Model;
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
/// Finding H7: a grouped aggregate emitted no LIMIT, so <c>groupBy: [id]</c> —
/// one group per row — read the whole table regardless of the model's
/// <c>max-query-rows</c> ceiling. The group window is now bounded by the SAME
/// <see cref="GqlObjectQuery.ClampRowLimit"/> ceiling as row queries, ordered
/// deterministically by the group keys so paging is stable, and the client's
/// <c>limit</c> can only NARROW that window — never raise it.
/// </summary>
public sealed class GroupedAggregateGroupCapTests
{
    public static IEnumerable<object[]> Dialects => new[]
    {
        new object[] { SqlServerDialect.Instance, SqlFlavor.SqlServer },
        new object[] { PostgresDialect.Instance, SqlFlavor.Postgres },
        new object[] { MySqlDialect.Instance, SqlFlavor.MySql },
        new object[] { SqliteDialect.Instance, SqlFlavor.Sqlite },
    };

    private static IDbModel BuildModel(int? maxQueryRows = null)
    {
        var fixture = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Region", "nvarchar")
                .WithColumn("Amount", "decimal"));
        if (maxQueryRows is not null)
            fixture = fixture.WithModelMetadata(MetadataKeys.Model.MaxQueryRows, maxQueryRows.Value.ToString());
        return fixture.Build();
    }

    private static string BuildSql(
        IDbModel model, ISqlDialect dialect, int? limit = null, int? offset = null, bool group = true)
    {
        var orders = model.GetTableFromDbName("Orders");
        var region = orders.Columns.Single(c => c.DbName == "Region");
        var amount = orders.Columns.Single(c => c.DbName == "Amount");

        var query = new GqlObjectQuery
        {
            DbTable = orders,
            TableName = "Orders",
            GraphQlName = "Orders",
            Path = "Orders",
            Limit = limit,
            Offset = offset,
            GroupedAggregate = new GroupedAggregate
            {
                GroupColumns = group
                    ? new[] { new AggregateGroupColumn(region, region.GraphQlName) }
                    : Array.Empty<AggregateGroupColumn>(),
                IncludeCount = true,
                ValueColumns = new[]
                {
                    new AggregateValueColumn(AggregateOperationType.Sum, amount, "_sum",
                        AggregateSurface.ValueAlias("_sum", amount.GraphQlName)),
                },
            },
        };

        var sqls = new Dictionary<string, ParameterizedSql>();
        query.AddSqlParameterized(model, dialect, sqls, new SqlParameterCollection());
        return sqls["Orders"].Sql;
    }

    /// <summary>The dialect's row-count bound for <paramref name="rows"/> groups.</summary>
    private static string Bound(SqlFlavor flavor, int rows) =>
        flavor == SqlFlavor.SqlServer ? $"FETCH NEXT {rows} ROWS ONLY" : $"LIMIT {rows}";

    [Theory]
    [MemberData(nameof(Dialects))]
    public void GroupedAggregate_WithoutClientLimit_IsBoundedAndDeterministicallyOrdered(
        ISqlDialect dialect, SqlFlavor flavor)
    {
        var sql = BuildSql(BuildModel(), dialect);

        SqlSyntax.AssertValid(sql, flavor, "bounded grouped aggregate SQL");
        sql.Should().Contain("GROUP BY");
        sql.Should().Contain(Bound(flavor, 100),
            "an unbounded grouped aggregate lets groupBy:[id] materialize the whole table");
        sql.Should().Contain("ORDER BY",
            "a LIMIT window over an unordered grouped result is undefined, so paging cannot be stable");
        sql.Should().Contain($"{dialect.EscapeIdentifier("Region")} asc",
            "the group keys are the deterministic order");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void GroupedAggregate_WithoutClientLimit_StillObeysALowerCeiling(ISqlDialect dialect, SqlFlavor flavor)
    {
        var sql = BuildSql(BuildModel(maxQueryRows: 50), dialect);

        SqlSyntax.AssertValid(sql, flavor, "ceiling-bounded grouped aggregate SQL");
        sql.Should().Contain(Bound(flavor, 50),
            "an operator who caps reads at 50 rows must not receive the 100-row default window");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void GroupedAggregate_NoLimitSentinel_ClampsToConfiguredCeiling(ISqlDialect dialect, SqlFlavor flavor)
    {
        var sql = BuildSql(BuildModel(maxQueryRows: 50), dialect, limit: -1);

        SqlSyntax.AssertValid(sql, flavor, "clamped grouped aggregate SQL");
        sql.Should().Contain(Bound(flavor, 50), "limit: -1 must not mean 'read every group'");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void GroupedAggregate_ClientLimitAboveCeiling_ClampsDown(ISqlDialect dialect, SqlFlavor flavor)
    {
        var sql = BuildSql(BuildModel(maxQueryRows: 50), dialect, limit: 5_000);

        SqlSyntax.AssertValid(sql, flavor, "clamped grouped aggregate SQL");
        sql.Should().Contain(Bound(flavor, 50), "the ceiling is a server ceiling; a client may only narrow it");
        sql.Should().NotContain("5000");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void GroupedAggregate_ClientLimitBelowCeiling_Narrows(ISqlDialect dialect, SqlFlavor flavor)
    {
        var sql = BuildSql(BuildModel(maxQueryRows: 50), dialect, limit: 10, offset: 20);

        SqlSyntax.AssertValid(sql, flavor, "paged grouped aggregate SQL");
        sql.Should().Contain(Bound(flavor, 10));
        sql.Should().Contain(flavor == SqlFlavor.SqlServer ? "OFFSET 20 ROWS" : "OFFSET 20");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void WholeTableAggregate_NeedsNoWindow(ISqlDialect dialect, SqlFlavor flavor)
    {
        var sql = BuildSql(BuildModel(), dialect, group: false);

        SqlSyntax.AssertValid(sql, flavor, "whole-table aggregate SQL");
        sql.Should().NotContain("GROUP BY");
        // Exactly one row by construction — a window would add cost and an
        // ORDER BY over no group key with nothing to order by.
        sql.Should().NotContain("LIMIT").And.NotContain("FETCH NEXT").And.NotContain("ORDER BY");
    }
}
