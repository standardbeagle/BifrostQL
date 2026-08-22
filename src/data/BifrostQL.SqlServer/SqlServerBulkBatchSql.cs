using System.Text;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers.BulkBatch;

namespace BifrostQL.SqlServer;

/// <summary>
/// T-SQL text for the set-based batch fast path. Staging column names carry a
/// <c>__c_</c> prefix so a transformer filter's unqualified target-column references can
/// never bind to the staging alias; staging/control names are server-generated (no user
/// input reaches an identifier unescaped — target identifiers come from the trusted schema
/// model and are bracket-escaped regardless).
/// </summary>
internal static class SqlServerBulkBatchSql
{
    internal const string SeqColumn = "__seq";
    internal const string OpColumn = "__op";
    internal const string GroupColumn = "__grp";
    internal const string ConflictColumn = "__conflict";
    internal const string StagedColumnPrefix = "__c_";
    internal const string ConflictErrorNumber = "51000";

    internal static string StagedColumn(string column) => StagedColumnPrefix + column;

    internal static char OpLetter(BulkOpCode op) => op switch
    {
        BulkOpCode.Insert => 'I',
        BulkOpCode.Update => 'U',
        BulkOpCode.Delete => 'D',
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    /// <summary>
    /// Staging DDL: <c>SELECT TOP 0 … INTO</c> clones every needed column's REAL target
    /// type (no type-mapping seam required), the clustered index keys the staging rows by
    /// batch position, and the out-table collects the seq of every update/delete row that
    /// matched a target row.
    /// </summary>
    public static string BuildStagingDdl(
        ISqlDialect dialect, string tableRef, string stagingName, string outName, IReadOnlyList<string> columns)
    {
        var sb = new StringBuilder();
        sb.Append("SELECT TOP 0 CAST(NULL AS INT) AS ").Append(dialect.EscapeIdentifier(SeqColumn));
        sb.Append(", CAST(NULL AS CHAR(1)) AS ").Append(dialect.EscapeIdentifier(OpColumn));
        sb.Append(", CAST(NULL AS TINYINT) AS ").Append(dialect.EscapeIdentifier(GroupColumn));
        sb.Append(", CAST(NULL AS BIT) AS ").Append(dialect.EscapeIdentifier(ConflictColumn));
        foreach (var column in columns)
            sb.Append(", t.").Append(dialect.EscapeIdentifier(column))
              .Append(" AS ").Append(dialect.EscapeIdentifier(StagedColumn(column)));
        sb.Append(" INTO ").Append(dialect.EscapeIdentifier(stagingName))
          .Append(" FROM ").Append(tableRef).Append(" t;\r\n");
        sb.Append("CREATE CLUSTERED INDEX ").Append(dialect.EscapeIdentifier("IX" + stagingName.TrimStart('#')))
          .Append(" ON ").Append(dialect.EscapeIdentifier(stagingName))
          .Append('(').Append(dialect.EscapeIdentifier(SeqColumn)).Append(");\r\n");
        sb.Append("CREATE TABLE ").Append(dialect.EscapeIdentifier(outName))
          // No PK: a single staged delete/update row may match several target rows (its
          // predicate need not be a full key), producing one seq entry per affected row.
          .Append('(').Append(dialect.EscapeIdentifier(SeqColumn)).Append(" INT NOT NULL);");
        return sb.ToString();
    }

    /// <summary>
    /// The whole batch application as ONE command: every group's set-based statement, the
    /// conflict check, and the commit run inside an inline SQL transaction
    /// (<see cref="ISqlDialect.BeginTransactionSql"/> … COMMIT) wrapped in TRY/CATCH with
    /// ROLLBACK + THROW, so any failure — including a concurrency conflict — applies
    /// nothing. Returns the accumulated database-reported affected-row total.
    /// </summary>
    public static string BuildDmlBatch(
        ISqlDialect dialect, string tableRef, string stagingName, string outName, IReadOnlyList<BulkOpGroup> groups)
    {
        var stage = dialect.EscapeIdentifier(stagingName);
        var outTable = dialect.EscapeIdentifier(outName);
        var seq = dialect.EscapeIdentifier(SeqColumn);

        var sb = new StringBuilder();
        sb.Append("SET NOCOUNT ON;\r\nDECLARE @bifrost_affected INT = 0;\r\nBEGIN TRY\r\n");
        sb.Append(dialect.BeginTransactionSql).Append("\r\n");
        foreach (var group in groups)
        {
            AppendGroupStatement(sb, dialect, tableRef, stage, outTable, group);
            sb.Append("SET @bifrost_affected += @@ROWCOUNT;\r\n");
        }
        sb.Append("IF EXISTS (SELECT 1 FROM ").Append(stage).Append(" s WHERE s.")
          .Append(dialect.EscapeIdentifier(ConflictColumn)).Append(" = 1 AND NOT EXISTS (SELECT 1 FROM ")
          .Append(outTable).Append(" o WHERE o.").Append(seq).Append(" = s.").Append(seq).Append("))\r\n")
          .Append("    THROW ").Append(ConflictErrorNumber).Append(", N'BIFROST_BULK_CONFLICT', 1;\r\n");
        sb.Append(dialect.CommitTransactionSql).Append("\r\nEND TRY\r\nBEGIN CATCH\r\n");
        sb.Append("IF @@TRANCOUNT > 0 ").Append(dialect.RollbackTransactionSql).Append("\r\nTHROW;\r\nEND CATCH;\r\n");
        sb.Append("SELECT @bifrost_affected;");
        return sb.ToString();
    }

    /// <summary>Reads back the matched update/delete seqs; runs after the commit on the same connection.</summary>
    public static string BuildAffectedSeqSelect(ISqlDialect dialect, string outName)
        => $"SELECT {dialect.EscapeIdentifier(SeqColumn)} FROM {dialect.EscapeIdentifier(outName)};";

    private static void AppendGroupStatement(
        StringBuilder sb, ISqlDialect dialect, string tableRef, string stage, string outTable, BulkOpGroup group)
    {
        var seq = dialect.EscapeIdentifier(SeqColumn);
        var opPredicate =
            $"s.{dialect.EscapeIdentifier(OpColumn)} = '{OpLetter(group.Op)}' AND s.{dialect.EscapeIdentifier(GroupColumn)} = {group.Id}";

        switch (group.Op)
        {
            case BulkOpCode.Insert:
                // Inserts never carry a transformer filter (mirroring the per-row path) and
                // apply in batch order.
                sb.Append("INSERT INTO ").Append(tableRef).Append('(')
                  .Append(string.Join(",", group.SetColumns.Select(dialect.EscapeIdentifier)))
                  .Append(") SELECT ")
                  .Append(string.Join(",", group.SetColumns.Select(c => $"s.{dialect.EscapeIdentifier(StagedColumn(c))}")))
                  .Append(" FROM ").Append(stage).Append(" s WHERE ").Append(opPredicate)
                  .Append(" ORDER BY s.").Append(seq).Append(";\r\n");
                break;

            case BulkOpCode.Update:
                sb.Append("UPDATE t SET ")
                  .Append(string.Join(",", group.SetColumns.Select(c =>
                      $"t.{dialect.EscapeIdentifier(c)} = s.{dialect.EscapeIdentifier(StagedColumn(c))}")))
                  .Append(" OUTPUT s.").Append(seq).Append(" INTO ").Append(outTable).Append('(').Append(seq).Append(')')
                  .Append(" FROM ").Append(tableRef).Append(" t INNER JOIN ").Append(stage).Append(" s ON ")
                  .Append(JoinPredicate(dialect, group.KeyColumns))
                  .Append(" WHERE ").Append(opPredicate).Append(group.FilterSql).Append(";\r\n");
                break;

            case BulkOpCode.Delete:
                sb.Append("DELETE t OUTPUT s.").Append(seq).Append(" INTO ").Append(outTable).Append('(').Append(seq).Append(')')
                  .Append(" FROM ").Append(tableRef).Append(" t INNER JOIN ").Append(stage).Append(" s ON ")
                  .Append(JoinPredicate(dialect, group.KeyColumns))
                  .Append(" WHERE ").Append(opPredicate).Append(group.FilterSql).Append(";\r\n");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(group), group.Op, null);
        }
    }

    private static string JoinPredicate(ISqlDialect dialect, IReadOnlyList<string> keyColumns)
        => string.Join(" AND ", keyColumns.Select(c =>
            $"t.{dialect.EscapeIdentifier(c)} = s.{dialect.EscapeIdentifier(StagedColumn(c))}"));
}
