using System.Text;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers.BulkBatch;
using static BifrostQL.Core.Resolvers.BulkBatch.StagedBulkBatchExecutorBase;

namespace BifrostQL.Ngsql;

/// <summary>
/// PostgreSQL's set-based batch capability on the shared staged flow
/// (<see cref="StagedBulkBatchExecutorBase"/>). SQL text lives in
/// <see cref="PostgresBulkBatchSql"/>.
/// </summary>
public sealed class PostgresBulkBatchExecutor : StagedBulkBatchExecutorBase
{
    public static PostgresBulkBatchExecutor Instance { get; } = new();

    protected override ISqlDialect Dialect => PostgresDialect.Instance;

    protected override IReadOnlyList<string> BuildStagingDdl(
        string tableRef, string stagingName, string outName, IReadOnlyList<string> columns)
        => PostgresBulkBatchSql.BuildStagingDdl(Dialect, tableRef, stagingName, outName, columns);

    protected override IReadOnlyList<StagedStatement> BuildGroupStatements(
        string tableRef, string stagingName, string outName, BulkOpGroup group)
        => PostgresBulkBatchSql.BuildGroupStatements(Dialect, tableRef, stagingName, outName, group);

    protected override string BuildConflictCheckSql(string stagingName, string outName)
        => PostgresBulkBatchSql.BuildConflictCheckSql(Dialect, stagingName, outName);
}

/// <summary>
/// PostgreSQL SQL text for the set-based batch fast path. The staging clone
/// (<c>CREATE TEMP TABLE … AS SELECT … WITH NO DATA</c>, NULLIF dropping NOT NULL while
/// keeping each column's real type) mirrors the SQL Server shape; updates and deletes run as
/// data-modifying CTEs whose <c>RETURNING s.__seq</c> lands the per-affected-row seq entries
/// in the out-table, so ONE statement both applies the write and names the affected staged
/// rows — the statement's DB-reported count is the group's affected total.
/// </summary>
internal static class PostgresBulkBatchSql
{
    public static IReadOnlyList<string> BuildStagingDdl(
        ISqlDialect d, string tableRef, string stagingName, string outName, IReadOnlyList<string> columns)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TEMP TABLE ").Append(d.EscapeIdentifier(stagingName))
          .Append(" AS SELECT CAST(NULL AS INT) AS ").Append(d.EscapeIdentifier(SeqColumn))
          .Append(", CAST(NULL AS CHAR(1)) AS ").Append(d.EscapeIdentifier(OpColumn))
          .Append(", CAST(NULL AS SMALLINT) AS ").Append(d.EscapeIdentifier(GroupColumn))
          .Append(", CAST(NULL AS BOOLEAN) AS ").Append(d.EscapeIdentifier(ConflictColumn));
        foreach (var column in columns)
        {
            // NULLIF(col, col) keeps the cloned column's exact type; the CTAS column carries
            // no NOT NULL constraint, so a staged row's unused columns stay NULL.
            var escaped = d.EscapeIdentifier(column);
            sb.Append(", NULLIF(t.").Append(escaped).Append(", t.").Append(escaped)
              .Append(") AS ").Append(d.EscapeIdentifier(StagedColumn(column)));
        }
        sb.Append(" FROM ").Append(tableRef).Append(" t WITH NO DATA;");

        return new[]
        {
            sb.ToString(),
            $"CREATE INDEX ON {d.EscapeIdentifier(stagingName)} ({d.EscapeIdentifier(SeqColumn)});",
            $"CREATE TEMP TABLE {d.EscapeIdentifier(outName)} ({d.EscapeIdentifier(SeqColumn)} INT NOT NULL);",
        };
    }

    public static IReadOnlyList<StagedStatement> BuildGroupStatements(
        ISqlDialect d, string tableRef, string stagingName, string outName, BulkOpGroup group)
    {
        var stage = d.EscapeIdentifier(stagingName);
        var outTable = d.EscapeIdentifier(outName);
        var seq = d.EscapeIdentifier(SeqColumn);
        var opPredicate =
            $"s.{d.EscapeIdentifier(OpColumn)} = '{OpLetter(group.Op)}' AND s.{d.EscapeIdentifier(GroupColumn)} = {group.Id}";

        switch (group.Op)
        {
            case BulkOpCode.Insert:
                return new[]
                {
                    new StagedStatement(
                        $"INSERT INTO {tableRef}({string.Join(",", group.SetColumns.Select(d.EscapeIdentifier))}) " +
                        $"SELECT {string.Join(",", group.SetColumns.Select(c => $"s.{d.EscapeIdentifier(StagedColumn(c))}"))} " +
                        $"FROM {stage} s WHERE {opPredicate} ORDER BY s.{seq};",
                        CountsTowardTotal: true, BindFilter: false),
                };

            case BulkOpCode.Update:
                var setClause = string.Join(",", group.SetColumns.Select(c =>
                    $"{d.EscapeIdentifier(c)} = s.{d.EscapeIdentifier(StagedColumn(c))}"));
                return new[]
                {
                    new StagedStatement(
                        $"WITH applied AS (UPDATE {tableRef} t SET {setClause} FROM {stage} s " +
                        $"WHERE {JoinPredicate(d, group.KeyColumns)} AND {opPredicate}{group.FilterSql} " +
                        $"RETURNING s.{seq} AS applied_seq) " +
                        $"INSERT INTO {outTable} SELECT applied_seq FROM applied;",
                        CountsTowardTotal: true, BindFilter: true),
                };

            case BulkOpCode.Delete:
                return new[]
                {
                    new StagedStatement(
                        $"WITH applied AS (DELETE FROM {tableRef} t USING {stage} s " +
                        $"WHERE {JoinPredicate(d, group.KeyColumns)} AND {opPredicate}{group.FilterSql} " +
                        $"RETURNING s.{seq} AS applied_seq) " +
                        $"INSERT INTO {outTable} SELECT applied_seq FROM applied;",
                        CountsTowardTotal: true, BindFilter: true),
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(group), group.Op, null);
        }
    }

    public static string BuildConflictCheckSql(ISqlDialect d, string stagingName, string outName)
        => $"SELECT EXISTS (SELECT 1 FROM {d.EscapeIdentifier(stagingName)} s " +
           $"WHERE s.{d.EscapeIdentifier(ConflictColumn)} AND NOT EXISTS " +
           $"(SELECT 1 FROM {d.EscapeIdentifier(outName)} o WHERE o.{d.EscapeIdentifier(SeqColumn)} = s.{d.EscapeIdentifier(SeqColumn)}));";

    private static string JoinPredicate(ISqlDialect d, IReadOnlyList<string> keyColumns)
        => string.Join(" AND ", keyColumns.Select(c =>
            $"t.{d.EscapeIdentifier(c)} = s.{d.EscapeIdentifier(StagedColumn(c))}"));
}
