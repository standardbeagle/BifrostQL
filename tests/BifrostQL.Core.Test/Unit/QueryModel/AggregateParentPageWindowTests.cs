using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using BifrostQL.SqlServer;
using BifrostQL.Testing;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// A per-row <c>_agg</c> statement must read only the parent rows on the caller's
/// page. Its first hop used to be <c>SELECT joinId, srcId FROM parent {filter}</c>:
/// the parent's filter but not its window, so <c>orders(limit: 100) { data { _agg } }</c>
/// grouped every matching parent in the table and <c>max-query-rows</c> never bound
/// it. The first hop must carry the parent SELECT's own window (same ORDER BY with
/// the key tie-break, offset and clamped limit) and close the derived table right
/// after it, so the GROUP BY sees one group per paged parent.
/// </summary>
public sealed class AggregateParentPageWindowTests
{
    private static GqlObjectQuery BuildUsersWithOrderCount(IDbModel dbModel, int? limit, int? offset)
    {
        var usersTable = dbModel.GetTableFromDbName("Users");
        var aggregate = new GqlAggregateColumn(
            new List<(LinkDirection, TableLinkDto)> { (LinkDirection.OneToMany, usersTable.MultiLinks["orders"]) },
            "Id",
            "orderCount",
            AggregateOperationType.Count);

        var builder = GqlObjectQueryBuilder.Create()
            .WithDbTable(usersTable)
            .WithColumns("Id", "Name")
            .WithSort("Name_desc")
            .WithAggregateColumn(aggregate);
        if (limit is { } l)
            builder.WithLimit(l);
        if (offset is { } o)
            builder.WithOffset(o);
        return builder.Build();
    }

    private static (string Parent, string Aggregate) Emit(IDbModel dbModel, ISqlDialect dialect, int? limit, int? offset)
    {
        var query = BuildUsersWithOrderCount(dbModel, limit, offset);
        var sqls = new Dictionary<string, ParameterizedSql>();
        query.AddSqlParameterized(dbModel, dialect, sqls, new SqlParameterCollection());
        return (sqls[query.KeyName].Sql, sqls[query.AggregateColumns.Single().SqlKey!].Sql);
    }

    /// <summary>The window the parent SELECT carries: caller sort, then the PK tie-break.</summary>
    private static string ParentWindow(ISqlDialect dialect, int offset, int limit)
        => dialect.Pagination(
            new[] { $"{dialect.EscapeIdentifier("Name")} desc", $"{dialect.EscapeIdentifier("Id")} asc" },
            offset,
            limit);

    /// <summary>
    /// The window followed by the close of the first-hop derived table: a window
    /// anywhere else (after GROUP BY, or inside a later hop) would not bound the
    /// parent rows the aggregate groups.
    /// </summary>
    private static string FirstHopClose(ISqlDialect dialect, int offset, int limit)
        => $"{ParentWindow(dialect, offset, limit)}) {dialect.EscapeIdentifier("src")} INNER JOIN ";

    [Theory]
    [MemberData(nameof(DialectFixtures.AllDialectData), MemberType = typeof(DialectFixtures))]
    public void ExplicitPage_AggregateFirstHop_CarriesTheParentWindow(ISqlDialect dialect)
    {
        var (parent, aggregate) = Emit(StandardTestFixtures.UsersWithOrders(), dialect, limit: 3, offset: 2);

        // Control: the parent SELECT ends in exactly this window.
        parent.Should().EndWith(ParentWindow(dialect, 2, 3));
        aggregate.Should().Contain(FirstHopClose(dialect, 2, 3),
            "the aggregate's first hop must be paged to the parent's window, or it groups every parent in the table");
    }

    [Theory]
    [MemberData(nameof(DialectFixtures.AllDialectData), MemberType = typeof(DialectFixtures))]
    public void NoLimit_UnderMaxQueryRows_AggregateFirstHopClampsToTheCeiling(ISqlDialect dialect)
    {
        var dbModel = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int"))
            .WithMultiLink("Users", "Id", "Orders", "UserId", "orders")
            .WithModelMetadata(MetadataKeys.Model.MaxQueryRows, "5")
            .Build();

        var (parent, aggregate) = Emit(dbModel, dialect, limit: null, offset: null);

        parent.Should().EndWith(ParentWindow(dialect, 0, 5));
        aggregate.Should().Contain(FirstHopClose(dialect, 0, 5),
            "max-query-rows must bind the aggregate's parent rows exactly as it binds the parent SELECT");
    }

    [Fact]
    public void ExplicitPage_SqlServer_AggregateSqlParses()
    {
        var (_, aggregate) = Emit(StandardTestFixtures.UsersWithOrders(), SqlServerDialect.Instance, limit: 3, offset: 2);

        SqlSyntax.AssertValid(aggregate, "a paged derived table inside the aggregate chain must be valid T-SQL");
    }
}
