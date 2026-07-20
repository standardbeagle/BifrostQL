using BifrostQL.Core.Modules.History;
using BifrostQL.Core.Modules.Retention;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Cross-dialect validation of the raw SQL the retention purge emits directly (not through the
/// query builder): the DISTINCT-tenant enumeration SELECT and the erasure trail-purge DELETE.
/// Both are built ENTIRELY through <see cref="ISqlDialect"/> (no dialect literal), so they must
/// render valid on all four dialects. The SQL Server form is checked against Microsoft's
/// ScriptDom parser; the others are checked structurally against their own delimiters — the same
/// split the existing SqlSyntax/CrossDialect harness uses.
/// </summary>
public sealed class RetentionErasureSqlDialectTests
{
    public static IEnumerable<object[]> AllDialectData => DialectFixtures.AllDialectData;

    private static void AssertValidTSql(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        parser.Parse(reader, out var errors);
        errors.Should().BeEmpty(
            $"generated SQL must parse as T-SQL: {string.Join("; ", errors.Select(e => $"Line {e.Line}: {e.Message}"))}\nSQL: {sql}");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void TrailPurgeSql_IsWellFormed_OnEveryDialect(ISqlDialect dialect)
    {
        var tableRef = dialect.TableReference("main", "__history");
        var sql = HistoryErasure.BuildTrailPurgeSql(dialect, tableRef);

        sql.Should().StartWith("DELETE FROM ");
        sql.Should().Contain("WHERE");
        // Scoped by the trail's entity + entity_id columns, each escaped in the dialect's delimiters.
        sql.Should().Contain(dialect.EscapeIdentifier("entity"));
        sql.Should().Contain(dialect.EscapeIdentifier("entity_id"));
        sql.Should().Contain("@entity");
        sql.Should().Contain("@entity_id");
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void TenantEnumerationSql_IsWellFormed_OnEveryDialect(ISqlDialect dialect)
    {
        var tableRef = dialect.TableReference("main", "people");
        var sql = RetentionPurgeEngine.BuildTenantEnumerationSql(dialect, tableRef, "tenant_id");

        sql.Should().StartWith("SELECT DISTINCT ");
        sql.Should().Contain("IS NOT NULL");
        sql.Should().Contain(dialect.EscapeIdentifier("tenant_id"));
    }

    [Fact]
    public void TrailPurgeSql_SqlServer_ParsesAsTSql()
        => AssertValidTSql(HistoryErasure.BuildTrailPurgeSql(
            SqlServerDialect.Instance, SqlServerDialect.Instance.TableReference("main", "__history")));

    [Fact]
    public void TenantEnumerationSql_SqlServer_ParsesAsTSql()
        => AssertValidTSql(RetentionPurgeEngine.BuildTenantEnumerationSql(
            SqlServerDialect.Instance, SqlServerDialect.Instance.TableReference("main", "people"), "tenant_id"));
}
