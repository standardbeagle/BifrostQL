using System.Text.RegularExpressions;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// A link field's rows are fetched by a SEPARATE statement that re-pages the parent
/// table down to its join-id columns (GqlObjectQuery.GetRestrictedSqlParameterized).
/// When the parent carries limit/offset, that statement's window and the parent
/// SELECT's window must land on exactly the same rows — otherwise the parent rows
/// handed back to the caller are correlated against join ids belonging to OTHER
/// rows and every link on the page resolves null/empty.
///
/// Regression: `posts(limit:3){ data { title authors { name } tags { data { name } } } }`
/// returned null authors and empty tags on SQLite. Neither statement had an ORDER BY,
/// so the two LIMIT windows were free to pick different rows — and did, as soon as the
/// two projections diverged enough for the engine to choose different access paths.
/// Selecting ONLY the join-id column made both plans agree and the bug vanish, which is
/// why it read as intermittent.
///
/// These tests assert the two ORDER BY clauses are identical and totally ordered, which
/// is what makes the two windows equal by construction rather than by plan luck.
/// </summary>
public sealed class PagedParentJoinAlignmentTests
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    /// <summary>Pulls the ORDER BY list out of a statement, stopping at the paging clause.</summary>
    private static string OrderByOf(string sql)
    {
        var match = Regex.Match(sql, @"ORDER BY (?<cols>.*?)(?= OFFSET | LIMIT |$)");
        match.Success.Should().BeTrue($"statement should be ordered for paging: {sql}");
        return match.Groups["cols"].Value.Trim();
    }

    /// <summary>
    /// Builds `parent(limit/offset/sort) { ...scalars, link { ... } }` and returns the
    /// parent row SELECT plus the restricted join-id sub-query the link fetch pages with.
    /// The parent selects extra scalar columns on purpose: identical projections were
    /// what accidentally hid the bug.
    /// </summary>
    private static (string Parent, string Restricted) BuildPagedQuery(
        IDbModel dbModel,
        string tableName,
        string linkName,
        int? limit = 3,
        int? offset = null,
        params string[] sort)
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
            ScalarColumns = { new GqlObjectColumn("Name"), new GqlObjectColumn("Email") },
            Links = { link },
            Limit = limit,
            Offset = offset,
            Sort = sort.ToList(),
        };
        query.ConnectLinks(dbModel);

        var sqls = new Dictionary<string, ParameterizedSql>();
        query.AddSqlParameterized(dbModel, Dialect, sqls, new SqlParameterCollection());

        var parent = sqls[query.KeyName].Sql;
        var restricted = sqls.Values.Select(s => s.Sql).Single(s => s.Contains("SELECT DISTINCT"));
        return (parent, restricted);
    }

    [Fact]
    public void UnsortedPagedParent_OrdersBothWindowsByPrimaryKey()
    {
        // Arrange / Act
        var (parent, restricted) = BuildPagedQuery(StandardTestFixtures.UsersWithOrders(), "Users", "orders");

        // Assert — a total order, and the SAME one on both statements.
        OrderByOf(parent).Should().Be("[Id] asc");
        OrderByOf(restricted).Should().Be(OrderByOf(parent));
    }

    [Fact]
    public void SortedPagedParent_AppendsPrimaryKeyAsTieBreakOnBothWindows()
    {
        // Arrange / Act — Name is not unique, so it alone does not order the rows.
        var (parent, restricted) = BuildPagedQuery(
            StandardTestFixtures.UsersWithOrders(), "Users", "orders", limit: 3, offset: null, sort: "Name_asc");

        // Assert
        OrderByOf(parent).Should().Be("[Name] asc, [Id] asc");
        OrderByOf(restricted).Should().Be(OrderByOf(parent));
    }

    [Fact]
    public void PagedParentSortedByItsKey_DoesNotRepeatTheKeyColumn()
    {
        // Arrange / Act
        var (parent, restricted) = BuildPagedQuery(
            StandardTestFixtures.UsersWithOrders(), "Users", "orders", limit: 3, offset: null, sort: "Id_desc");

        // Assert
        OrderByOf(parent).Should().Be("[Id] desc");
        OrderByOf(restricted).Should().Be(OrderByOf(parent));
    }

    [Fact]
    public void OffsetPagedParent_AlignsBothWindows()
    {
        // Arrange / Act — offset alone (no limit) also pages, and drifts the same way.
        var (parent, restricted) = BuildPagedQuery(
            StandardTestFixtures.UsersWithOrders(), "Users", "orders", limit: null, offset: 10);

        // Assert
        OrderByOf(parent).Should().Be("[Id] asc");
        OrderByOf(restricted).Should().Be(OrderByOf(parent));
    }

    [Fact]
    public void CompositeKeyParent_OrdersBothWindowsByEveryKeyColumn()
    {
        // Arrange — a composite PK needs ALL its columns to be a total order; ordering
        // by the first alone leaves the tie among its duplicates free to differ.
        var dbModel = DbModelTestFixture.Create()
            .WithTable("Memberships", t => t
                .WithPrimaryKey("OrgId")
                .WithPrimaryKey("UserId")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar"))
            .WithTable("Visits", t => t
                .WithPrimaryKey("Id")
                .WithColumn("OrgId", "int")
                .WithColumn("Name", "nvarchar"))
            .WithMultiLink("Memberships", "OrgId", "Visits", "OrgId", "visits")
            .Build();

        // Act
        var (parent, restricted) = BuildPagedQuery(dbModel, "Memberships", "visits");

        // Assert
        OrderByOf(parent).Should().Be("[OrgId] asc, [UserId] asc");
        OrderByOf(restricted).Should().Be(OrderByOf(parent));
    }

    [Fact]
    public void KeylessParent_InventsNoOrderBeyondTheCallersSort()
    {
        // Arrange — a view has no key to tie-break on. There is no total order to
        // synthesize, so the caller's sort is used as-is rather than a guessed column.
        var dbModel = DbModelTestFixture.Create()
            .WithTable("UserSummary", t => t
                .WithColumn("UserId", "int")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Email", "nvarchar"))
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("Name", "nvarchar"))
            .WithMultiLink("UserSummary", "UserId", "Orders", "UserId", "orders")
            .Build();

        // Act
        var (parent, restricted) = BuildPagedQuery(
            dbModel, "UserSummary", "orders", limit: 3, offset: null, sort: "Name_asc");

        // Assert
        OrderByOf(parent).Should().Be("[Name] asc");
        OrderByOf(restricted).Should().Be(OrderByOf(parent));
    }
}
