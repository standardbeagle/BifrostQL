using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers.BulkBatch;
using BifrostQL.SqlServer;
using BifrostQL.Testing;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Resolvers;

public sealed class SqlServerBulkBatchSqlTests
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;
    private const string Stage = "#bifrost_batch_abc";
    private const string Out = "#bifrost_out_abc";

    private static string TableRef => Dialect.TableReference("dbo", "Orders");

    private static IReadOnlyList<BulkOpGroup> SampleGroups(string filterSql = "", params SqlParameterInfo[] filterParams) => new[]
    {
        new BulkOpGroup(BulkOpCode.Insert, 0, new[] { "Status", "Total" }, Array.Empty<string>(), "", Array.Empty<SqlParameterInfo>()),
        new BulkOpGroup(BulkOpCode.Update, 1, new[] { "Status" }, new[] { "OrderId", "LineNo" }, filterSql, filterParams),
        new BulkOpGroup(BulkOpCode.Delete, 2, Array.Empty<string>(), new[] { "OrderId", "LineNo" }, filterSql, filterParams),
    };

    [Fact]
    public void StagingDdl_Parses_AndClonesRealColumnTypes()
    {
        var sql = SqlServerBulkBatchSql.BuildStagingDdl(Dialect, TableRef, Stage, Out, new[] { "Status", "Total" });

        SqlSyntax.AssertValid(sql, "staging DDL");
        // SELECT TOP 0 … INTO clones the target's real types — no type-mapping seam.
        sql.Should().Contain("SELECT TOP 0");
        sql.Should().Contain($"INTO [{Stage}]");
        sql.Should().Contain("CREATE CLUSTERED INDEX");
        sql.Should().Contain("[__seq]").And.Contain("[__op]").And.Contain("[__grp]").And.Contain("[__conflict]");
        // Staged data columns carry the prefix so filter references can never bind to them,
        // and NULLIF drops the target's NOT NULL constraint while keeping its type.
        sql.Should().Contain("NULLIF(t.[Status], t.[Status]) AS [__c_Status]");
        sql.Should().Contain("NULLIF(t.[Total], t.[Total]) AS [__c_Total]");
    }

    [Fact]
    public void DmlBatch_Parses_WithInlineTransactionShape()
    {
        var sql = SqlServerBulkBatchSql.BuildDmlBatch(Dialect, TableRef, Stage, Out, SampleGroups());

        SqlSyntax.AssertValid(sql, "DML batch");
        sql.Should().Contain("BEGIN TRY");
        sql.Should().Contain("BEGIN TRANSACTION;");
        sql.Should().Contain("COMMIT;");
        sql.Should().Contain("BEGIN CATCH");
        sql.Should().Contain("IF @@TRANCOUNT > 0 ROLLBACK;");
        sql.Should().Contain("THROW;");
        sql.Should().Contain("OUTPUT s.[__seq] INTO");
        sql.Should().Contain("SELECT @bifrost_affected;");
    }

    [Fact]
    public void DmlBatch_EachOpFiltersItsOwnRowsAndJoinsAllKeyColumns()
    {
        var sql = SqlServerBulkBatchSql.BuildDmlBatch(Dialect, TableRef, Stage, Out, SampleGroups());

        sql.Should().Contain("s.[__op] = 'I' AND s.[__grp] = 0");
        sql.Should().Contain("s.[__op] = 'U' AND s.[__grp] = 1");
        sql.Should().Contain("s.[__op] = 'D' AND s.[__grp] = 2");
        // Composite key: every key column participates in the join, on both statements.
        sql.Should().Contain("t.[OrderId] = s.[__c_OrderId] AND t.[LineNo] = s.[__c_LineNo]");
        sql.Should().Contain("INSERT INTO [dbo].[Orders]([Status],[Total]) SELECT s.[__c_Status],s.[__c_Total]");
        sql.Should().Contain("ORDER BY s.[__seq]");
        sql.Should().Contain("UPDATE t SET t.[Status] = s.[__c_Status]");
        sql.Should().Contain("DELETE t OUTPUT");
    }

    [Fact]
    public void DmlBatch_ConflictCheck_ThrowsInsideTransaction()
    {
        var sql = SqlServerBulkBatchSql.BuildDmlBatch(Dialect, TableRef, Stage, Out, SampleGroups());

        var throwIndex = sql.IndexOf("THROW 51000", StringComparison.Ordinal);
        var commitIndex = sql.IndexOf("COMMIT;", StringComparison.Ordinal);
        throwIndex.Should().BePositive("the conflict check must exist");
        throwIndex.Should().BeLessThan(commitIndex, "a conflict must abort before the commit");
        sql.Should().Contain("s.[__conflict] = 1 AND NOT EXISTS");
    }

    [Fact]
    public void DmlBatch_TransformerFilter_AppendsAsParameterizedSuffix()
    {
        var filter = " AND ([TenantId] = @p0)";
        var sql = SqlServerBulkBatchSql.BuildDmlBatch(
            Dialect, TableRef, Stage, Out, SampleGroups(filter, new SqlParameterInfo("@p0", 7)));

        SqlSyntax.AssertValid(sql, "DML batch with filter");
        sql.Should().Contain("s.[__op] = 'U' AND s.[__grp] = 1 AND ([TenantId] = @p0);");
        sql.Should().Contain("s.[__op] = 'D' AND s.[__grp] = 2 AND ([TenantId] = @p0);");
        // The tenant VALUE never appears in the SQL text — only its parameter marker.
        sql.Should().NotContain("7");
    }

    [Fact]
    public void AffectedSeqSelect_Parses()
    {
        var sql = SqlServerBulkBatchSql.BuildAffectedSeqSelect(Dialect, Out);
        SqlSyntax.AssertValid(sql, "seq read-back");
        sql.Should().Be($"SELECT [__seq] FROM [{Out}];");
    }
}
