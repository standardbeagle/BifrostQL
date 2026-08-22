using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers.BulkBatch;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Testing;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Resolvers;

/// <summary>
/// SQL-text coverage for the PostgreSQL and MySQL staged bulk batch builders, mirroring
/// <see cref="SqlServerBulkBatchSqlTests"/>: statements parse under the provider's grammar
/// (where SqlParserCS supports the construct), each op filters its own staged rows, every
/// key column joins, and transformer filters appear only as parameter markers.
/// </summary>
public sealed class StagedBulkBatchSqlTests
{
    private const string Stage = "bifrost_batch_abc";
    private const string Out = "bifrost_out_abc";

    private static IReadOnlyList<BulkOpGroup> SampleGroups(string filterSql = "", params SqlParameterInfo[] filterParams) => new[]
    {
        new BulkOpGroup(BulkOpCode.Insert, 0, new[] { "status", "total" }, Array.Empty<string>(), "", Array.Empty<SqlParameterInfo>()),
        new BulkOpGroup(BulkOpCode.Update, 1, new[] { "status" }, new[] { "order_id", "line_no" }, filterSql, filterParams),
        new BulkOpGroup(BulkOpCode.Delete, 2, Array.Empty<string>(), new[] { "order_id", "line_no" }, filterSql, filterParams),
    };

    // ---- PostgreSQL ----

    private static ISqlDialect Pg => PostgresDialect.Instance;
    private static string PgTableRef => Pg.TableReference("public", "orders");

    [Fact]
    public void Postgres_StagingDdl_ClonesTypesNullableWithControlColumns()
    {
        var ddl = PostgresBulkBatchSql.BuildStagingDdl(Pg, PgTableRef, Stage, Out, new[] { "status", "total" });

        ddl.Should().HaveCount(3);
        ddl[0].Should().Contain("CREATE TEMP TABLE \"bifrost_batch_abc\" AS SELECT");
        ddl[0].Should().Contain("WITH NO DATA");
        ddl[0].Should().Contain("\"__seq\"").And.Contain("\"__op\"").And.Contain("\"__grp\"").And.Contain("\"__conflict\"");
        ddl[0].Should().Contain("NULLIF(t.\"status\", t.\"status\") AS \"__c_status\"");
        ddl[1].Should().Contain("CREATE INDEX ON \"bifrost_batch_abc\" (\"__seq\")");
        ddl[2].Should().Be("CREATE TEMP TABLE \"bifrost_out_abc\" (\"__seq\" INT NOT NULL);");
    }

    [Fact]
    public void Postgres_GroupStatements_ApplyAndRecordSeqsInOneStatement()
    {
        var statements = SampleGroups(" AND (\"tenant_id\" = @p0)", new SqlParameterInfo("@p0", 7))
            .SelectMany(g => PostgresBulkBatchSql.BuildGroupStatements(Pg, PgTableRef, Stage, Out, g))
            .ToList();

        statements.Should().HaveCount(3);
        statements.Should().OnlyContain(s => s.CountsTowardTotal, "every PG group applies in one counted statement");

        var insert = statements[0].Sql;
        SqlSyntax.AssertValid(insert, SqlFlavor.Postgres, "PG insert");
        insert.Should().Contain("s.\"__op\" = 'I' AND s.\"__grp\" = 0");
        insert.Should().Contain("ORDER BY s.\"__seq\"");
        statements[0].BindFilter.Should().BeFalse();

        var update = statements[1].Sql;
        update.Should().StartWith("WITH applied AS (UPDATE ");
        update.Should().Contain("RETURNING s.\"__seq\" AS applied_seq");
        update.Should().Contain("INSERT INTO \"bifrost_out_abc\" SELECT applied_seq FROM applied");
        update.Should().Contain("t.\"order_id\" = s.\"__c_order_id\" AND t.\"line_no\" = s.\"__c_line_no\"");
        update.Should().Contain("s.\"__op\" = 'U' AND s.\"__grp\" = 1 AND (\"tenant_id\" = @p0)");

        var delete = statements[2].Sql;
        delete.Should().Contain("DELETE FROM ").And.Contain(" USING \"bifrost_batch_abc\" s");
        delete.Should().Contain("RETURNING s.\"__seq\" AS applied_seq");
        delete.Should().Contain("s.\"__op\" = 'D' AND s.\"__grp\" = 2 AND (\"tenant_id\" = @p0)");
        // The tenant VALUE never appears in SQL text — only its parameter marker.
        update.Should().NotContain("7");
        delete.Should().NotContain("7");
    }

    [Fact]
    public void Postgres_ConflictCheck_ProbesConflictRowsAbsentFromOut()
    {
        var sql = PostgresBulkBatchSql.BuildConflictCheckSql(Pg, Stage, Out);
        SqlSyntax.AssertValid(sql, SqlFlavor.Postgres, "PG conflict check");
        sql.Should().Contain("s.\"__conflict\" AND NOT EXISTS");
        sql.Should().Contain("o.\"__seq\" = s.\"__seq\"");
    }

    // ---- MySQL ----

    private static ISqlDialect My => MySqlDialect.Instance;
    private static string MyTableRef => My.TableReference("", "orders");

    [Fact]
    public void MySql_StagingDdl_ClonesTypesNullableWithControlColumns()
    {
        var ddl = MySqlBulkBatchSql.BuildStagingDdl(My, MyTableRef, Stage, Out, new[] { "status", "total" });

        ddl.Should().HaveCount(3);
        ddl[0].Should().Contain("CREATE TEMPORARY TABLE `bifrost_batch_abc` AS SELECT");
        ddl[0].Should().Contain("WHERE 1=0");
        ddl[0].Should().Contain("`__seq`").And.Contain("`__op`").And.Contain("`__grp`").And.Contain("`__conflict`");
        ddl[0].Should().Contain("NULLIF(t.`status`, t.`status`) AS `__c_status`");
        ddl[1].Should().Contain("ADD INDEX (`__seq`)");
        ddl[2].Should().Be("CREATE TEMPORARY TABLE `bifrost_out_abc` (`__seq` INT NOT NULL);");
    }

    [Fact]
    public void MySql_UpdateAndDelete_ProbeThenWrite()
    {
        var filter = " AND (`tenant_id` = @p0)";
        var groups = SampleGroups(filter, new SqlParameterInfo("@p0", 7));

        var insertStatements = MySqlBulkBatchSql.BuildGroupStatements(My, MyTableRef, Stage, Out, groups[0]);
        insertStatements.Should().HaveCount(1);
        SqlSyntax.AssertValid(insertStatements[0].Sql, SqlFlavor.MySql, "MySQL insert");
        insertStatements[0].CountsTowardTotal.Should().BeTrue();

        var updateStatements = MySqlBulkBatchSql.BuildGroupStatements(My, MyTableRef, Stage, Out, groups[1]);
        updateStatements.Should().HaveCount(2);
        // The probe records — and FOR UPDATE row-locks — the matched staged rows first,
        // and must never count toward the affected total.
        var probe = updateStatements[0];
        probe.CountsTowardTotal.Should().BeFalse();
        probe.BindFilter.Should().BeTrue();
        probe.Sql.Should().StartWith($"INSERT INTO `bifrost_out_abc` SELECT s.`__seq` FROM ");
        probe.Sql.Should().EndWith("FOR UPDATE;");
        probe.Sql.Should().Contain("s.`__op` = 'U' AND s.`__grp` = 1 AND (`tenant_id` = @p0)");

        var write = updateStatements[1];
        write.CountsTowardTotal.Should().BeTrue();
        write.Sql.Should().Contain("UPDATE ").And.Contain(" INNER JOIN `bifrost_batch_abc` s ON ");
        write.Sql.Should().Contain("t.`order_id` = s.`__c_order_id` AND t.`line_no` = s.`__c_line_no`");
        write.Sql.Should().Contain("SET t.`status` = s.`__c_status`");

        var deleteStatements = MySqlBulkBatchSql.BuildGroupStatements(My, MyTableRef, Stage, Out, groups[2]);
        deleteStatements.Should().HaveCount(2);
        deleteStatements[0].CountsTowardTotal.Should().BeFalse();
        deleteStatements[1].Sql.Should().StartWith("DELETE t FROM ");
        deleteStatements[1].Sql.Should().Contain("s.`__op` = 'D' AND s.`__grp` = 2 AND (`tenant_id` = @p0)");
        // The tenant VALUE never appears in SQL text — only its parameter marker.
        updateStatements.Concat(deleteStatements).Should().OnlyContain(s => !s.Sql.Contains("7"));
    }

    [Fact]
    public void MySql_ConflictCheck_ProbesConflictRowsAbsentFromOut()
    {
        var sql = MySqlBulkBatchSql.BuildConflictCheckSql(My, Stage, Out);
        SqlSyntax.AssertValid(sql, SqlFlavor.MySql, "MySQL conflict check");
        sql.Should().Contain("s.`__conflict` = 1 AND NOT EXISTS");
        sql.Should().Contain("o.`__seq` = s.`__seq`");
    }
}
