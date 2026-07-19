using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.QueryModel;

/// <summary>
/// FTS slice 2: per-dialect lowering of the table-scoped <c>_search</c> operator. Proves,
/// across all four dialects, that (a) the query TERMS are bound as SQL parameters (a
/// placeholder appears, the literal term does not), (b) only the validated FtsConfig
/// columns are referenced, (c) multi-term input is ANDed per the pinned semantic, and
/// (d) the generated SQL is syntactically valid for the target engine. A dialect that did
/// not override <c>SearchPredicate</c> would fail to compile (it is abstract on
/// <see cref="SqlDialectBase"/>), which is the "cannot silently inherit a non-searching
/// predicate" guarantee.
/// </summary>
public class FtsSearchPredicateTests
{
    private static IDbModel SearchableModel() =>
        DbModelTestFixture.Create()
            .WithTable("Articles", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Title", "nvarchar")
                .WithColumn("Body", "text")
                .WithMetadata(MetadataKeys.Fts.Search, "Title,Body"))
            .Build();

    private static (string sql, IReadOnlyList<SqlParameterInfo> parameters) RenderSearch(
        ISqlDialect dialect, string search)
    {
        var model = SearchableModel();
        var filter = TableFilter.FromObject(
            new Dictionary<string, object?> { { FilterOperators.Search, search } }, "Articles");
        var parameters = new SqlParameterCollection();
        var rendered = filter.ToSqlParameterized(model, dialect, parameters, "Articles");
        return (rendered.Sql, rendered.Parameters.ToList());
    }

    public static IEnumerable<object[]> Dialects() => new[]
    {
        new object[] { SqlServerDialect.Instance, SqlFlavor.SqlServer },
        new object[] { PostgresDialect.Instance, SqlFlavor.Postgres },
        new object[] { MySqlDialect.Instance, SqlFlavor.MySql },
        new object[] { SqliteDialect.Instance, SqlFlavor.Sqlite },
    };

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Search_BindsTermsAsParameters_NotLiterals(ISqlDialect dialect, SqlFlavor flavor)
    {
        // Act
        var (sql, parameters) = RenderSearch(dialect, "quick brown");

        // Assert: the user terms are bound values, never spliced into SQL text.
        sql.Should().Contain(dialect.ParameterPrefix, "the terms must be bound parameters");
        sql.Should().NotContain("quick");
        sql.Should().NotContain("brown");
        parameters.Should().HaveCount(2, "two terms -> two bound parameters");
        // The bound VALUES carry the (neutralized) term text.
        parameters.Select(p => p.Value?.ToString() ?? "")
            .Should().OnlyContain(v => v.Contains("quick") || v.Contains("brown"));
        _ = flavor;
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Search_MultiTerm_IsAndedNotOred(ISqlDialect dialect, SqlFlavor flavor)
    {
        var (sql, _) = RenderSearch(dialect, "quick brown");

        // The pinned semantic: terms are conjunctive. The two per-term predicates are
        // joined by AND, never OR (an OR would widen the result set).
        sql.Should().Contain(" AND ");
        sql.Should().NotContain(" OR ");
        _ = flavor;
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Search_ReferencesOnlyConfiguredColumns(ISqlDialect dialect, SqlFlavor flavor)
    {
        var (sql, _) = RenderSearch(dialect, "quick");

        if (flavor == SqlFlavor.Sqlite)
        {
            // SQLite FTS5 indexes the columns in an external-content companion table named
            // after the base table (schema-derived, never client input); the predicate
            // references that table, not the columns directly.
            sql.Should().Contain("Articles_fts");
        }
        else
        {
            sql.Should().Contain("Title");
            sql.Should().Contain("Body");
        }
    }

    // SqlServer output is validated with Microsoft's ScriptDom and Postgres output with
    // SqlParserCS; both accept their engines' full-text syntax (CONTAINS, to_tsvector @@
    // tsquery). SqlParserCS does NOT model MySQL's `AGAINST(… IN BOOLEAN MODE)` modifier
    // nor SQLite's `MATCH` operator, so those two engines' full-text predicates cannot be
    // round-tripped through the harness — they are covered structurally below and, for
    // SQLite, behaviorally against a real FTS5 database in the Integration suite.
    public static IEnumerable<object[]> ParsableDialects() => new[]
    {
        new object[] { SqlServerDialect.Instance, SqlFlavor.SqlServer },
        new object[] { PostgresDialect.Instance, SqlFlavor.Postgres },
    };

    [Theory]
    [MemberData(nameof(ParsableDialects))]
    public void Search_GeneratesSyntacticallyValidSql(ISqlDialect dialect, SqlFlavor flavor)
    {
        var (sql, _) = RenderSearch(dialect, "quick brown");
        var tableRef = dialect.TableReference(null, "Articles");

        SqlSyntax.AssertValid($"SELECT * FROM {tableRef} WHERE {sql}", flavor,
            "the full-text predicate must be valid SQL for the engine");
    }

    [Fact]
    public void Search_MySql_EmitsBooleanModeMatchAgainst()
    {
        // Structural coverage for MySQL (the harness parser cannot model IN BOOLEAN MODE):
        // MATCH over the configured columns, boolean mode (so SQL-level AND honors the
        // pinned multi-term semantic), one bound parameter per term.
        var (sql, parameters) = RenderSearch(MySqlDialect.Instance, "quick brown");
        sql.Should().Contain("MATCH(`Title`, `Body`) AGAINST(@p0 IN BOOLEAN MODE)");
        sql.Should().Contain("AGAINST(@p1 IN BOOLEAN MODE)");
        parameters.Should().HaveCount(2);
    }

    [Fact]
    public void Search_Sqlite_EmitsFts5MatchCorrelatedByKey()
    {
        // Structural coverage for SQLite (the harness parser cannot model the MATCH
        // operator): each term correlates the base-table key against an FTS5 rowid MATCH
        // subquery over the <table>_fts companion index, ANDed across terms.
        var (sql, parameters) = RenderSearch(SqliteDialect.Instance, "quick brown");
        sql.Should().Contain("\"Articles\".\"Id\" IN (SELECT \"rowid\" FROM \"Articles_fts\" WHERE \"Articles_fts\" MATCH @p0)");
        sql.Should().Contain("MATCH @p1)");
        parameters.Should().HaveCount(2);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Search_PredicateIsParenthesized(ISqlDialect dialect, SqlFlavor flavor)
    {
        // The whole search predicate is wrapped so it can never bind more loosely than a
        // security filter ANDed with it.
        var (sql, _) = RenderSearch(dialect, "quick brown");
        sql.Should().StartWith("(").And.EndWith(")");
        _ = flavor;
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Search_EmptyQuery_AddsNoPredicate(ISqlDialect dialect, SqlFlavor flavor)
    {
        // An empty/whitespace search must not constrain (or open) the query — it
        // contributes no predicate at all.
        var (sql, parameters) = RenderSearch(dialect, "   ");
        sql.Should().BeEmpty();
        parameters.Should().BeEmpty();
        _ = flavor;
    }

    [Fact]
    public void Search_Sqlite_CompositePrimaryKey_FailsClosedWithActionableError()
    {
        // FTS5 external-content correlates on a single integer rowid, so a composite PK
        // cannot be searched — fail closed with a clear prerequisite message.
        var model = DbModelTestFixture.Create()
            .WithTable("Rollup", t => t
                .WithColumn("TenantId", "int", isPrimaryKey: true)
                .WithColumn("Sku", "nvarchar", isPrimaryKey: true)
                .WithColumn("Notes", "nvarchar")
                .WithMetadata(MetadataKeys.Fts.Search, "Notes"))
            .Build();
        var filter = TableFilter.FromObject(
            new Dictionary<string, object?> { { FilterOperators.Search, "widget" } }, "Rollup");
        var parameters = new SqlParameterCollection();

        var act = () => filter.ToSqlParameterized(model, SqliteDialect.Instance, parameters, "Rollup");

        act.Should().Throw<BifrostExecutionError>().WithMessage("*single-column primary key*");
    }

    [Fact]
    public void Search_Postgres_PhraseUsesPhraseToTsquery()
    {
        // A double-quoted run is a contiguous phrase: Postgres must use phraseto_tsquery
        // (adjacent) rather than plainto_tsquery (bag of words).
        var (sql, _) = RenderSearch(PostgresDialect.Instance, "\"lazy dog\"");
        sql.Should().Contain("phraseto_tsquery");
        sql.Should().NotContain("plainto_tsquery");
    }

    [Fact]
    public void Search_Postgres_WordUsesPlainToTsquery()
    {
        var (sql, _) = RenderSearch(PostgresDialect.Instance, "lazy dog");
        sql.Should().Contain("plainto_tsquery");
        sql.Should().NotContain("phraseto_tsquery");
    }
}
