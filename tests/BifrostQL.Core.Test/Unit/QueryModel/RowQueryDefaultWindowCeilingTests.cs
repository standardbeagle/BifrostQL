using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using BifrostQL.Testing;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.QueryModel;

/// <summary>
/// H7 follow-up (01M1MT73CNJM4B8PTMQV846P9Y), pinned alongside M9: an unspecified
/// <c>limit</c> resolves to <see cref="GqlObjectQuery.DefaultRowWindow"/> BEFORE the
/// <c>max-query-rows</c> clamp on every row-read surface — the root SELECT, the
/// restricted join-id sub-query, and the per-parent paged collection window — so an
/// operator ceiling below 100 binds the default page. Previously
/// <c>ClampRowLimit(null)</c> returned null and the dialect's own null → 100 default
/// (<see cref="ISqlDialect.Pagination"/> / <see cref="ISqlDialect.ConnectedPaging"/>)
/// bypassed the ceiling: <c>max-query-rows: 5</c> still read 100 rows.
/// </summary>
public sealed class RowQueryDefaultWindowCeilingTests
{
    private const int Ceiling = 5;

    public static IEnumerable<object[]> Dialects => new[]
    {
        new object[] { SqlServerDialect.Instance, SqlFlavor.SqlServer },
        new object[] { PostgresDialect.Instance, SqlFlavor.Postgres },
        new object[] { MySqlDialect.Instance, SqlFlavor.MySql },
        new object[] { SqliteDialect.Instance, SqlFlavor.Sqlite },
    };

    private static IDbModel BuildModel(int ceiling = Ceiling) =>
        DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int"))
            .WithMultiLink("Users", "Id", "Orders", "UserId", "orders")
            .WithModelMetadata(MetadataKeys.Model.MaxQueryRows, ceiling.ToString())
            .Build();

    /// <summary>Root <c>Users { orders }</c>; the collection is the paged (IncludeResult) shape.</summary>
    private static Dictionary<string, ParameterizedSql> BuildSqls(
        IDbModel model, ISqlDialect dialect, int? rootLimit = null, int? childLimit = null)
    {
        var link = new GqlObjectQuery
        {
            GraphQlName = "orders",
            ScalarColumns = { new GqlObjectColumn("Id") },
            IncludeResult = true,
            Limit = childLimit,
        };
        var query = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName("Users"),
            TableName = "Users",
            GraphQlName = "Users",
            ScalarColumns = { new GqlObjectColumn("Name") },
            Links = { link },
            Limit = rootLimit,
        };
        query.ConnectLinks(model);

        var sqls = new Dictionary<string, ParameterizedSql>();
        query.AddSqlParameterized(model, dialect, sqls, new SqlParameterCollection());
        return sqls;
    }

    private static string RootSql(Dictionary<string, ParameterizedSql> sqls) => sqls["Users"].Sql;

    private static string ChildSql(Dictionary<string, ParameterizedSql> sqls) =>
        sqls.Single(kv => kv.Key.Contains("->")).Value.Sql;

    private static string Bound(SqlFlavor flavor, int rows) =>
        flavor == SqlFlavor.SqlServer ? $"FETCH NEXT {rows} ROWS ONLY" : $"LIMIT {rows}";

    [Theory]
    [MemberData(nameof(Dialects))]
    public void RootQuery_WithoutLimit_IsBoundedByACeilingBelowTheDefaultWindow(ISqlDialect dialect, SqlFlavor flavor)
    {
        var sql = RootSql(BuildSqls(BuildModel(), dialect));

        SqlSyntax.AssertValid(sql, flavor, "ceiling-bounded root row SQL");
        sql.Should().Contain(Bound(flavor, Ceiling),
            "an operator who caps reads at 5 rows must not receive the 100-row default window");
        sql.Should().NotContain(Bound(flavor, GqlObjectQuery.DefaultRowWindow));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void RootQuery_ExplicitLimitBelowCeiling_Narrows(ISqlDialect dialect, SqlFlavor flavor)
    {
        var sql = RootSql(BuildSqls(BuildModel(), dialect, rootLimit: 2));

        SqlSyntax.AssertValid(sql, flavor, "narrowed root row SQL");
        sql.Should().Contain(Bound(flavor, 2));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void RootQuery_ZeroLimit_StaysEmptyBounded(ISqlDialect dialect, SqlFlavor flavor)
    {
        var sql = RootSql(BuildSqls(BuildModel(), dialect, rootLimit: 0));

        SqlSyntax.AssertValid(sql, flavor, "empty-bounded root row SQL");
        sql.Should().Contain(Bound(flavor, 0), "limit: 0 is an empty page, not the default window");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void PagedCollection_WithoutLimit_PerParentWindowIsBoundedByTheCeiling(ISqlDialect dialect, SqlFlavor flavor)
    {
        var sql = ChildSql(BuildSqls(BuildModel(), dialect));

        SqlSyntax.AssertValid(sql, flavor, "ceiling-bounded per-parent collection SQL");
        // ConnectedPaging renders the per-parent window as a row-number band.
        sql.Should().Contain($"BETWEEN 1 AND {Ceiling}",
            "the per-parent window resolves its 100-row default before the clamp, so a ceiling of 5 binds it");
        sql.Should().NotContain($"BETWEEN 1 AND {GqlObjectQuery.DefaultRowWindow}");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void PagedCollection_ExplicitLimitBelowCeiling_Narrows(ISqlDialect dialect, SqlFlavor flavor)
    {
        var sql = ChildSql(BuildSqls(BuildModel(), dialect, childLimit: 2));

        SqlSyntax.AssertValid(sql, flavor, "narrowed per-parent collection SQL");
        sql.Should().Contain("BETWEEN 1 AND 2");
    }
}
