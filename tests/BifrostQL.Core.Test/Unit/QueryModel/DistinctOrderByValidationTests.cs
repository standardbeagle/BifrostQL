using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Guards against the SQL Server binding rule "ORDER BY items must appear in the
/// select list if SELECT DISTINCT is specified." ScriptDom only validates *syntax*
/// — this is a semantic/binding rule that parses cleanly and that SQLite tolerates,
/// so it slips past both the parser test and the SQLite integration suite. The lint
/// below walks the generated SQL's AST and fails when any DISTINCT query orders by a
/// column that is not in its own projection.
///
/// Regression: a parent table with paging (offset/limit) + sort, joined to a child
/// collection, produced `SELECT DISTINCT {join-id} ... ORDER BY {pk}` where the pk
/// sort columns were absent from the DISTINCT join-id projection. See
/// GqlObjectQuery.GetRestrictedSqlParameterized (query.Parent == null branch).
/// </summary>
public sealed class DistinctOrderByValidationTests
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    #region Lint

    /// <summary>
    /// Visits every QuerySpecification and records a violation when it is
    /// SELECT DISTINCT and orders by a column reference that is not produced by its
    /// own SELECT list (a `SELECT *` projection is treated as covering everything).
    /// </summary>
    private sealed class DistinctOrderByVisitor : TSqlFragmentVisitor
    {
        public readonly List<string> Violations = new();

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.UniqueRowFilter == UniqueRowFilter.Distinct && node.OrderByClause is not null)
            {
                var hasStar = node.SelectElements.OfType<SelectStarExpression>().Any();
                if (!hasStar)
                {
                    var projected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var se in node.SelectElements.OfType<SelectScalarExpression>())
                    {
                        // Alias wins (the name visible to ORDER BY); else the column's last identifier.
                        if (se.ColumnName?.Value is { } alias && alias.Length > 0)
                            projected.Add(alias);
                        if (se.Expression is ColumnReferenceExpression col && LastName(col) is { } n)
                            projected.Add(n);
                    }

                    foreach (var ob in node.OrderByClause.OrderByElements)
                    {
                        if (ob.Expression is ColumnReferenceExpression col && LastName(col) is { } name
                            && !projected.Contains(name))
                        {
                            Violations.Add($"DISTINCT query orders by '{name}' which is not in its projection [{string.Join(", ", projected)}]");
                        }
                    }
                }
            }
            base.ExplicitVisit(node);
        }

        private static string? LastName(ColumnReferenceExpression col)
            => col.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
    }

    private static void AssertNoDistinctOrderByViolation(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var parseErrors);
        parseErrors.Should().BeEmpty($"SQL must parse.\nSQL: {sql}");

        var visitor = new DistinctOrderByVisitor();
        fragment.Accept(visitor);
        visitor.Violations.Should().BeEmpty(
            $"generated SQL must not pair SELECT DISTINCT with an unprojected ORDER BY column.\nSQL: {sql}");
    }

    [Fact]
    public void Lint_FlagsKnownBadPattern()
    {
        // Sanity-check the lint itself catches the exact broken shape.
        const string bad = "SELECT DISTINCT [UserId] FROM [dbo].[Orders] ORDER BY [Id] OFFSET 10 ROWS FETCH NEXT 20 ROWS ONLY";
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(bad);
        var fragment = parser.Parse(reader, out _);
        var visitor = new DistinctOrderByVisitor();
        fragment.Accept(visitor);
        visitor.Violations.Should().NotBeEmpty("the lint must flag DISTINCT + unprojected ORDER BY");
    }

    [Fact]
    public void Lint_PassesWhenOrderByColumnIsProjected()
    {
        const string ok = "SELECT DISTINCT [Id] FROM [dbo].[Orders] ORDER BY [Id] OFFSET 10 ROWS FETCH NEXT 20 ROWS ONLY";
        AssertNoDistinctOrderByViolation(ok);
    }

    #endregion

    #region Regression: paged parent + child collection

    private static ParameterizedSql RestrictedSqlForPagedParent(
        IDbModel model, string parentTable, string multiLinkName, int? offset, int? limit, params string[] sort)
    {
        var link = new GqlObjectQuery { GraphQlName = multiLinkName, ScalarColumns = { new GqlObjectColumn("Id") } };
        var root = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName(parentTable),
            TableName = parentTable,
            GraphQlName = parentTable,
            ScalarColumns = { new GqlObjectColumn("Id") },
            Links = { link },
            Offset = offset,
            Limit = limit,
            Sort = sort.ToList(),
        };
        root.ConnectLinks(model);
        var join = root.Joins.Single();
        return GqlObjectQuery.GetRestrictedSqlParameterized(model, Dialect, new SqlParameterCollection(),
            new QueryLink(join, root, parent: null));
    }

    [Fact]
    public void PagedParentWithSort_DoesNotOrderByUnprojectedColumn()
    {
        // Parent Orders paged (offset+limit) and sorted by pk [Id], with link "user".
        // The join's FROM column is the FK [UserId] (≠ the [Id] sort), so the projection
        // holds [UserId] AS JoinId. Pre-fix this emitted
        //   SELECT DISTINCT [UserId] AS JoinId FROM [Orders] ORDER BY [Id] OFFSET .. FETCH ..
        // — [Id] not in the DISTINCT projection → SQL Server binding error.
        var model = StandardTestFixtures.UsersWithOrders();
        var sql = RestrictedSqlForPagedParent(model, "Orders", "user", offset: 10, limit: 20, "Id_asc");
        AssertNoDistinctOrderByViolation(sql.Sql);
    }

    [Theory]
    [InlineData(0, 20, "Id_asc")]   // limit only
    [InlineData(40, -1, "Id_desc")] // offset only (limit = -1 sentinel)
    [InlineData(5, 15, "Id_asc")]   // both
    public void PagedParent_VariousPaging_NoDistinctOrderByViolation(int offset, int limit, string sort)
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var sql = RestrictedSqlForPagedParent(model, "Orders", "user", offset, limit, sort);
        AssertNoDistinctOrderByViolation(sql.Sql);
    }

    [Fact]
    public void UnboundedParent_StillNoViolation()
    {
        // No paging → plain SELECT DISTINCT with no ORDER BY; must remain clean.
        var model = StandardTestFixtures.UsersWithOrders();
        var sql = RestrictedSqlForPagedParent(model, "Orders", "user", offset: null, limit: null);
        AssertNoDistinctOrderByViolation(sql.Sql);
    }

    #endregion

    #region Corpus hunt — lint every SQL emitted for representative query shapes

    private static void LintAllGeneratedSql(IDbModel model, GqlObjectQuery root)
    {
        root.ConnectLinks(model);
        var sqls = new Dictionary<string, ParameterizedSql>();
        root.AddSqlParameterized(model, Dialect, sqls, new SqlParameterCollection());
        sqls.Should().NotBeEmpty();
        foreach (var (key, sql) in sqls)
            AssertNoDistinctOrderByViolation($"/* {key} */ {sql.Sql}");
    }

    [Fact]
    public void Corpus_SingleLinkOffPagedParent_NoViolation()
    {
        // The regression class: FK projection ([UserId]) ≠ pk sort ([Id]).
        var model = StandardTestFixtures.UsersWithOrders();
        var root = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName("Orders"), TableName = "Orders", GraphQlName = "Orders",
            ScalarColumns = { new GqlObjectColumn("Id") }, Offset = 10, Limit = 20, Sort = { "Id_asc" },
            Links = { new GqlObjectQuery { GraphQlName = "user", ScalarColumns = { new GqlObjectColumn("Id") } } },
        };
        LintAllGeneratedSql(model, root);
    }

    [Fact]
    public void Corpus_PagedCollectionOffPagedParent_NoViolation()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var root = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName("Users"), TableName = "Users", GraphQlName = "Users",
            ScalarColumns = { new GqlObjectColumn("Id") }, Offset = 0, Limit = 20, Sort = { "Id_asc" },
            Links = { new GqlObjectQuery { GraphQlName = "orders", IncludeResult = true, Limit = 5, Sort = { "Id_desc" }, ScalarColumns = { new GqlObjectColumn("Id") } } },
        };
        LintAllGeneratedSql(model, root);
    }

    [Fact]
    public void Corpus_NestedPagedCollections_NoViolation()
    {
        // Two levels of paged collections — exercises the nested (Parent != null)
        // restricted-SQL branch in addition to the root branch.
        var model = StandardTestFixtures.CompanyHierarchy();
        var root = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName("Companies"), TableName = "Companies", GraphQlName = "Companies",
            ScalarColumns = { new GqlObjectColumn("Id") }, Offset = 5, Limit = 10, Sort = { "Id_asc" },
            Links =
            {
                new GqlObjectQuery
                {
                    GraphQlName = "departments", IncludeResult = true, Limit = 5, Sort = { "Id_asc" }, ScalarColumns = { new GqlObjectColumn("Id") },
                    Links = { new GqlObjectQuery { GraphQlName = "employees", IncludeResult = true, Limit = 3, Sort = { "Id_asc" }, ScalarColumns = { new GqlObjectColumn("Id") } } },
                },
            },
        };
        LintAllGeneratedSql(model, root);
    }

    #endregion
}
