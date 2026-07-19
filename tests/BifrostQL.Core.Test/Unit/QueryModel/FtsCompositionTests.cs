using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.QueryModel;

/// <summary>
/// FTS slice 3 (composition — the security-critical part): the table-scoped <c>_search</c>
/// predicate must be ANDed INSIDE <see cref="QueryTransformerService"/>'s composition with
/// the tenant (band 0-99) and soft-delete (band 100-199) filter transformers — never ORed,
/// replaced, or short-circuited — and the multi-column search predicate must be wrapped so
/// it can never bind more loosely than the security filters it is ANDed with. An OR or a
/// mis-parenthesized <c>tenant = @t AND a MATCH x OR b MATCH y</c> would leak across
/// tenants; these tests assert the emitted SQL's shape per dialect.
/// </summary>
public class FtsCompositionTests
{
    private static IDbModel Model() =>
        DbModelTestFixture.Create()
            .WithTable("Articles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Title", "nvarchar")
                .WithColumn("Body", "text")
                .WithColumn("TenantId", "int")
                .WithColumn("DeletedAt", "datetime2", isNullable: true)
                .WithMetadata(MetadataKeys.Fts.Search, "Title,Body")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "TenantId")
                .WithMetadata(MetadataKeys.SoftDelete.Column, "DeletedAt"))
            .Build();

    private static QueryTransformerService Service() =>
        new(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new TenantFilterTransformer(),      // band 0-99 (security)
                new SoftDeleteFilterTransformer(),  // band 100-199 (data filtering)
            },
        });

    // Renders the composed WHERE for a _search query after the security transformers run.
    private static string ComposedSql(ISqlDialect dialect)
    {
        var model = Model();
        var articles = model.GetTableFromDbName("Articles");
        var node = GqlObjectQueryBuilder.Create()
            .WithDbTable(articles)
            .WithColumns("Id", "Title")
            .WithFilter(TableFilter.FromObject(
                new Dictionary<string, object?> { { FilterOperators.Search, "quick brown" } }, "Articles"))
            .Build();

        Service().ApplyTransformers(node, model, new Dictionary<string, object?> { ["tenant_id"] = 7 });

        return node.GetFilterSqlParameterized(model, dialect, new SqlParameterCollection(), "Articles").Sql;
    }

    public static IEnumerable<object[]> DialectsWithMarker() => new[]
    {
        new object[] { SqlServerDialect.Instance, "CONTAINS" },
        new object[] { PostgresDialect.Instance, "@@" },
        new object[] { MySqlDialect.Instance, "AGAINST" },
        new object[] { SqliteDialect.Instance, "MATCH" },
    };

    [Theory]
    [MemberData(nameof(DialectsWithMarker))]
    public void Search_IsAndedWithTenantAndSoftDeleteFilters(ISqlDialect dialect, string ftsMarker)
    {
        var sql = ComposedSql(dialect);

        // All three predicates present: the search, the tenant scope, and the soft-delete.
        sql.Should().Contain(ftsMarker, "the full-text predicate must be present");
        sql.Should().Contain("TenantId", "the tenant scope must be ANDed in");
        sql.Should().Contain("IS NULL", "the soft-delete predicate must be ANDed in");

        // Conjunctive composition only — never a disjunction that could widen the scope.
        sql.Should().Contain(" AND ");
        sql.Should().NotContain(" OR ");
    }

    [Theory]
    [MemberData(nameof(DialectsWithMarker))]
    public void Search_PredicateCannotBindMoreLooselyThanSecurityFilters(ISqlDialect dialect, string ftsMarker)
    {
        var sql = ComposedSql(dialect);

        // The search branch is wrapped by RenderSearchParts and again by the AND combine,
        // so the full-text marker is nested at least two parens deep — it can never sit at
        // the same precedence as the surrounding AND (the mis-parenthesization that leaks).
        var markerIndex = sql.IndexOf(ftsMarker, System.StringComparison.Ordinal);
        markerIndex.Should().BeGreaterThan(0);
        var prefix = sql[..markerIndex];
        (prefix.Count(c => c == '(') - prefix.Count(c => c == ')'))
            .Should().BeGreaterThanOrEqualTo(2,
                "the search predicate must be enclosed in the security-filter AND, not a loosely-bound sibling");
    }
}
