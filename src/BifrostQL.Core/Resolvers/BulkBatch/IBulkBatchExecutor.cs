using System.Data.Common;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Resolvers.BulkBatch
{
    /// <summary>The wire operation a staged row applies: the letter that lands in the staging table's op column.</summary>
    public enum BulkOpCode
    {
        Insert,
        Update,
        Delete,
    }

    /// <summary>
    /// One transformer-processed batch action, ready for staging. <see cref="Values"/> is keyed by
    /// REAL DB column names (transformer output already rekeyed via the pipeline) — for an insert
    /// the columns to insert, for an update the SET columns plus the key columns, for a delete the
    /// WHERE columns. <see cref="Seq"/> is the action's position in the original batch (the staging
    /// clustered-index key); <see cref="Group"/> is the column-signature group within the op, so
    /// rows writing different column sets never share a set-based statement.
    /// </summary>
    public sealed record BulkStagedAction(
        int Seq,
        BulkOpCode Op,
        int Group,
        bool ConflictOnNoRows,
        IReadOnlyDictionary<string, object?> Values);

    /// <summary>
    /// One set-based statement to emit: every staged row with this op + group is applied by a
    /// single INSERT…SELECT / UPDATE…JOIN / DELETE…JOIN. <see cref="FilterSql"/> is the
    /// transformer chain's rendered additional filter (empty, or a leading <c>" AND (…)"</c>
    /// suffix exactly as <c>MutationCommandExecutor.RenderAdditionalFilter</c> produces it) —
    /// identical for every row in the group, verified by the plan builder; its parameters bind on
    /// the command, never into staging columns.
    /// </summary>
    public sealed record BulkOpGroup(
        BulkOpCode Op,
        int Id,
        IReadOnlyList<string> SetColumns,
        IReadOnlyList<string> KeyColumns,
        string FilterSql,
        IReadOnlyList<SqlParameterInfo> FilterParameters);

    /// <summary>
    /// A fully transformed batch, ready for a dialect's set-based execution: load
    /// <see cref="Rows"/> into a staging table cloned from the target, then apply each
    /// <see cref="Groups"/> statement inside ONE inline SQL transaction. Column and table names
    /// are raw DB identifiers from the trusted schema model; the executor escapes them with its
    /// own dialect.
    /// </summary>
    public sealed record BulkBatchPlan(
        string TableSchema,
        string TableDbName,
        IReadOnlyList<string> StagingColumns,
        IReadOnlyList<BulkStagedAction> Rows,
        IReadOnlyList<BulkOpGroup> Groups);

    /// <summary>
    /// The set-based outcome: <see cref="TotalAffected"/> is the database-reported affected-row
    /// total across every statement (the AffectedRows contract — never inferred), and
    /// <see cref="AffectedSeqs"/> holds the staging sequence numbers of update/delete rows that
    /// matched a target row (inserts always affect exactly one row and are not listed).
    /// </summary>
    public sealed record BulkBatchResult(int TotalAffected, IReadOnlySet<int> AffectedSeqs);

    /// <summary>
    /// A dialect's set-based batch capability: stage the plan's rows on the given open
    /// connection and apply them inside one inline SQL transaction
    /// (<c>BEGIN TRANSACTION … COMMIT</c>, rolled back atomically on any failure). A row whose
    /// plan demanded <see cref="BulkStagedAction.ConflictOnNoRows"/> but matched no target row
    /// aborts the whole batch with the pipeline's CONFLICT error. Exposed per conn-factory via
    /// <see cref="Model.IDbConnFactory.BulkBatchExecutor"/>; a factory without one keeps the
    /// per-row batch path.
    /// </summary>
    public interface IBulkBatchExecutor
    {
        Task<BulkBatchResult> ExecuteAsync(BulkBatchPlan plan, DbConnection connection, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Raised by an executor for a failure during the STAGING phase — staging-table DDL or the
    /// bulk load — before the inline transaction touches the target table. Nothing was written
    /// (the staging table dies with the connection), so the pipeline may safely retry the batch
    /// on the per-row path. A failure after the target DML begins must never use this type.
    /// </summary>
    public sealed class BulkBatchStagingException : Exception
    {
        public BulkBatchStagingException(string message, Exception inner) : base(message, inner) { }
    }
}
