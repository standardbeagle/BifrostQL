using System.Text;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers.BulkBatch;
using static BifrostQL.Core.Resolvers.BulkBatch.StagedBulkBatchExecutorBase;

namespace BifrostQL.MySql;

/// <summary>
/// MySQL's set-based batch capability on the shared staged flow
/// (<see cref="StagedBulkBatchExecutorBase"/>). SQL text lives in
/// <see cref="MySqlBulkBatchSql"/>.
/// </summary>
public sealed class MySqlBulkBatchExecutor : StagedBulkBatchExecutorBase
{
    public static MySqlBulkBatchExecutor Instance { get; } = new();

    protected override ISqlDialect Dialect => MySqlDialect.Instance;

    protected override IReadOnlyList<string> BuildStagingDdl(
        string tableRef, string stagingName, string outName, IReadOnlyList<string> columns)
        => MySqlBulkBatchSql.BuildStagingDdl(Dialect, tableRef, stagingName, outName, columns);

    protected override IReadOnlyList<StagedStatement> BuildGroupStatements(
        string tableRef, string stagingName, string outName, BulkOpGroup group)
        => MySqlBulkBatchSql.BuildGroupStatements(Dialect, tableRef, stagingName, outName, group);

    protected override string BuildConflictCheckSql(string stagingName, string outName)
        => MySqlBulkBatchSql.BuildConflictCheckSql(Dialect, stagingName, outName);
}

/// <summary>
/// MySQL SQL text for the set-based batch fast path. The staging clone
/// (<c>CREATE TEMPORARY TABLE … AS SELECT … WHERE 1=0</c>, NULLIF dropping NOT NULL while
/// keeping each column's type) mirrors the SQL Server shape. MySQL has no OUTPUT/RETURNING
/// on UPDATE/DELETE, so each write is preceded by a locking probe
/// (<c>INSERT INTO out … SELECT … FOR UPDATE</c>) that records — and row-locks — the staged
/// rows the write will match inside the same transaction; the write's own DB-reported count
/// is what sums into the total.
/// </summary>
internal static class MySqlBulkBatchSql
{
    public static IReadOnlyList<string> BuildStagingDdl(
        ISqlDialect d, string tableRef, string stagingName, string outName, IReadOnlyList<string> columns)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TEMPORARY TABLE ").Append(d.EscapeIdentifier(stagingName))
          .Append(" AS SELECT CAST(NULL AS SIGNED) AS ").Append(d.EscapeIdentifier(SeqColumn))
          .Append(", CAST(NULL AS CHAR(1)) AS ").Append(d.EscapeIdentifier(OpColumn))
          .Append(", CAST(NULL AS SIGNED) AS ").Append(d.EscapeIdentifier(GroupColumn))
          .Append(", CAST(NULL AS SIGNED) AS ").Append(d.EscapeIdentifier(ConflictColumn));
        foreach (var column in columns)
        {
            // NULLIF(col, col) keeps the cloned column's type; the CTAS column carries no
            // NOT NULL constraint, so a staged row's unused columns stay NULL.
            var escaped = d.EscapeIdentifier(column);
            sb.Append(", NULLIF(t.").Append(escaped).Append(", t.").Append(escaped)
              .Append(") AS ").Append(d.EscapeIdentifier(StagedColumn(column)));
        }
        sb.Append(" FROM ").Append(tableRef).Append(" t WHERE 1=0;");

        return new[]
        {
            sb.ToString(),
            // InnoDB index keys the staged rows by batch position for the joins.
            $"ALTER TABLE {d.EscapeIdentifier(stagingName)} ADD INDEX ({d.EscapeIdentifier(SeqColumn)});",
            $"CREATE TEMPORARY TABLE {d.EscapeIdentifier(outName)} ({d.EscapeIdentifier(SeqColumn)} INT NOT NULL);",
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
                    $"t.{d.EscapeIdentifier(c)} = s.{d.EscapeIdentifier(StagedColumn(c))}"));
                return new[]
                {
                    Probe(d, tableRef, stage, outTable, seq, group, opPredicate),
                    new StagedStatement(
                        $"UPDATE {tableRef} t INNER JOIN {stage} s ON {JoinPredicate(d, group.KeyColumns)} " +
                        $"SET {setClause} WHERE {opPredicate}{group.FilterSql};",
                        CountsTowardTotal: true, BindFilter: true),
                };

            case BulkOpCode.Delete:
                return new[]
                {
                    Probe(d, tableRef, stage, outTable, seq, group, opPredicate),
                    new StagedStatement(
                        $"DELETE t FROM {tableRef} t INNER JOIN {stage} s ON {JoinPredicate(d, group.KeyColumns)} " +
                        $"WHERE {opPredicate}{group.FilterSql};",
                        CountsTowardTotal: true, BindFilter: true),
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(group), group.Op, null);
        }
    }

    // FOR UPDATE row-locks the matched target rows, so the probe's recorded seq set cannot
    // drift from what the write that follows it affects.
    private static StagedStatement Probe(
        ISqlDialect d, string tableRef, string stage, string outTable, string seq, BulkOpGroup group, string opPredicate)
        => new(
            $"INSERT INTO {outTable} SELECT s.{seq} FROM {tableRef} t INNER JOIN {stage} s " +
            $"ON {JoinPredicate(d, group.KeyColumns)} WHERE {opPredicate}{group.FilterSql} FOR UPDATE;",
            CountsTowardTotal: false, BindFilter: true);

    public static string BuildConflictCheckSql(ISqlDialect d, string stagingName, string outName)
        => $"SELECT EXISTS (SELECT 1 FROM {d.EscapeIdentifier(stagingName)} s " +
           $"WHERE s.{d.EscapeIdentifier(ConflictColumn)} = 1 AND NOT EXISTS " +
           $"(SELECT 1 FROM {d.EscapeIdentifier(outName)} o WHERE o.{d.EscapeIdentifier(SeqColumn)} = s.{d.EscapeIdentifier(SeqColumn)}));";

    private static string JoinPredicate(ISqlDialect d, IReadOnlyList<string> keyColumns)
        => string.Join(" AND ", keyColumns.Select(c =>
            $"t.{d.EscapeIdentifier(c)} = s.{d.EscapeIdentifier(StagedColumn(c))}"));
}
