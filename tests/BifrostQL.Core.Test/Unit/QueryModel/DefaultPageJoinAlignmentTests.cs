using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.SqlServer;
using BifrostQL.Testing;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Finding M9: a link field's restricted join-id sub-query
/// (<see cref="GqlObjectQuery.GetRestrictedSqlParameterized"/>) must carry the SAME
/// effective page as the parent SELECT. When the parent names no <c>limit</c>, the
/// parent SELECT is paged to the dialect default (100) — but the restricted query
/// treated <c>ClampRowLimit(null) == null</c> as "unbounded" and emitted a bare
/// <c>SELECT DISTINCT {join-ids} FROM parent</c> spanning ALL N parents. Every child
/// statement then ran for parents the caller never receives, and ReaderEnum discarded
/// the rest. The fix resolves the parent's effective page ONCE
/// (<c>Limit ?? GqlObjectQuery.DefaultRowWindow</c>, clamped) and uses that same bound
/// for both statements.
/// </summary>
public sealed class DefaultPageJoinAlignmentTests
{
    private static readonly ISqlDialect SqlServer = SqlServerDialect.Instance;

    /// <summary>The window each dialect renders for the effective default page.</summary>
    public static IEnumerable<object[]> DialectWindows
    {
        get
        {
            foreach (var d in DialectFixtures.AllDialects)
            {
                var window = d is SqlServerDialect ? "FETCH NEXT 100 ROWS ONLY" : "LIMIT 100";
                yield return new object[] { d, window };
            }
        }
    }

    /// <summary>
    /// Builds `parent { ...scalars, link { ... } }` with no explicit limit and returns
    /// the parent row SELECT plus the restricted join-id sub-query itself — taken from
    /// <see cref="GqlObjectQuery.GetRestrictedSqlParameterized"/> directly, NOT by
    /// substring-searching the emitted statement set: the flat-collection statement
    /// embeds the restricted sub-query AND appends its own child pagination, so a
    /// "contains LIMIT 100" probe against it substring-matches the clamped -1 sentinel
    /// (<c>LIMIT 10000</c>) and the bug stays invisible.
    /// </summary>
    private static (string Parent, string Restricted) BuildUnlimitedQuery(
        IDbModel dbModel, ISqlDialect dialect, string tableName, string linkName, int? limit = null)
    {
        var link = new GqlObjectQuery
        {
            GraphQlName = linkName,
            ScalarColumns = { new GqlObjectColumn("Id") },
        };

        var query = new GqlObjectQuery
        {
            DbTable = dbModel.GetTableFromDbName(tableName),
            TableName = tableName,
            GraphQlName = tableName,
            ScalarColumns = { new GqlObjectColumn("Name") },
            Links = { link },
            Limit = limit,
        };
        query.ConnectLinks(dbModel);

        var sqls = new Dictionary<string, ParameterizedSql>();
        query.AddSqlParameterized(dbModel, dialect, sqls, new SqlParameterCollection());

        var join = query.Joins.Single();
        var restricted = GqlObjectQuery.GetRestrictedSqlParameterized(
            dbModel, dialect, new SqlParameterCollection(), new QueryLink(join, query, parent: null));

        return (sqls[query.KeyName].Sql, restricted.Sql);
    }

    [Theory]
    [MemberData(nameof(DialectWindows))]
    public void UnlimitedParent_RestrictedJoinQuery_CarriesTheSameDefaultPage(ISqlDialect dialect, string window)
    {
        // Arrange / Act — no limit on the parent.
        var (parent, restricted) = BuildUnlimitedQuery(
            StandardTestFixtures.UsersWithOrders(), dialect, "Users", "orders");

        // Assert — the parent pages to the default window, and the restricted join-id
        // query carries the SAME bound instead of spanning every parent row.
        parent.Should().Contain(window, "the parent SELECT is paged to the dialect default when no limit is given");
        restricted.Should().Contain(window,
            "the child join-id query must be paged to the parent's effective window — " +
            "an unpaged SELECT DISTINCT spans all N parents and runs the child statements for rows the caller never receives");
    }

    [Fact]
    public void UnlimitedParent_SqlServer_RestrictedQuery_ParsesCleanly()
    {
        // Arrange / Act
        var (_, restricted) = BuildUnlimitedQuery(
            StandardTestFixtures.UsersWithOrders(), SqlServer, "Users", "orders");

        // Assert — ScriptDom validates the wrapped DISTINCT-over-paged-inner shape.
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(restricted);
        parser.Parse(reader, out var errors);
        errors.Should().BeEmpty($"restricted join-id SQL must parse.\nSQL: {restricted}");
    }

    [Fact]
    public void ZeroLimitParent_RestrictedJoinQuery_IsEmptyBounded()
    {
        // Arrange / Act — limit: 0 yields an empty parent page; the child must be
        // empty-bounded too, not unbounded.
        var (parent, restricted) = BuildUnlimitedQuery(
            StandardTestFixtures.UsersWithOrders(), SqlServer, "Users", "orders", limit: 0);

        // Assert
        parent.Should().Contain("FETCH NEXT 0 ROWS ONLY");
        restricted.Should().Contain("FETCH NEXT 0 ROWS ONLY",
            "limit: 0 must bound the child join-id query to the same empty window");
    }

    [Fact]
    public void UnlimitedParent_WithMaxQueryRowsCeiling_BothWindowsClampToTheCeiling()
    {
        // Arrange — an operator ceiling below the default window must bind BOTH
        // statements; a null limit resolved downstream of the clamp would read 100
        // rows under a ceiling of 5.
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

        // Act
        var (parent, restricted) = BuildUnlimitedQuery(dbModel, SqlServer, "Users", "orders");

        // Assert
        parent.Should().Contain("FETCH NEXT 5 ROWS ONLY");
        restricted.Should().Contain("FETCH NEXT 5 ROWS ONLY");
    }
}
