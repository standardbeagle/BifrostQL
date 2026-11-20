using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using Col = BifrostQL.Core.Model.MetadataKeys.OperationId.Column;

namespace BifrostQL.Core.Modules.OperationId
{
    /// <summary>
    /// The SQL against the operation-id store (<see cref="MetadataKeys.OperationId.Store"/>).
    /// Every statement runs on the connection and transaction of the write it records, so the
    /// record commits with the write or not at all.
    /// </summary>
    internal static class OperationIdStore
    {
        /// <summary>
        /// The committed record under <paramref name="key"/>, read on the write's transaction;
        /// null when there is none.
        /// </summary>
        public static async Task<OperationRecord?> FindAsync(
            IDbTable store, ISqlDialect dialect, DbConnection connection, DbTransaction transaction,
            OperationRecordKey key, CancellationToken cancellationToken)
        {
            var row = await MutationCommandExecutor.LoadRowByKey(
                connection, transaction, dialect, store, RecordColumns, key.Columns(), cancellationToken: cancellationToken);
            if (row is null)
                return null;
            return new OperationRecord(
                new OperationFingerprint(Text(row, Col.Table), Text(row, Col.Action), Text(row, Col.TargetKey), Text(row, Col.InputHash)),
                Text(row, Col.Result));
        }

        private static readonly string[] RecordColumns = { Col.Table, Col.Action, Col.TargetKey, Col.InputHash, Col.Result };

        private static string Text(IReadOnlyDictionary<string, object?> row, string column)
            => row[column] as string
               ?? throw new BifrostExecutionError("The operation record is incomplete, so the write did not run.");

        /// <summary>
        /// Inserts the record under <paramref name="key"/> before the write runs, with no
        /// result yet. The store's unique key over (tenant, caller, operation id, field) makes a
        /// second transaction claiming the same id for the same field wait for the first. If the first commits, the
        /// waiting claim fails with a unique violation; <see cref="OperationIdExecution.ExecuteAsync"/>
        /// then reads the committed outcome in a fresh transaction. If the first rolls back,
        /// the waiting claim succeeds and the second transaction runs the write.
        /// </summary>
        public static async Task ClaimAsync(
            IDbTable store, ISqlDialect dialect, DbConnection connection, DbTransaction transaction,
            OperationRecordKey key, OperationFingerprint fingerprint, CancellationToken cancellationToken)
        {
            var row = key.Columns();
            row[Col.Table] = fingerprint.Table;
            row[Col.Action] = fingerprint.Action;
            row[Col.TargetKey] = fingerprint.TargetKey;
            row[Col.InputHash] = fingerprint.InputHash;
            row[Col.CreatedAt] = DateTime.UtcNow;
            var tableRef = dialect.TableReference(store.TableSchema, store.DbName);
            var sql = MutationCommandExecutor.BuildInsertInto(dialect, store, tableRef, row.Keys) + ";";
            await MutationCommandExecutor.ExecuteNonQuery(connection, transaction, sql, row, cancellationToken: cancellationToken);
        }

        /// <summary>Writes the mutation field's result onto the claimed record.</summary>
        public static async Task RecordResultAsync(
            IDbTable store, ISqlDialect dialect, DbConnection connection, DbTransaction transaction,
            OperationRecordKey key, string resultJson, CancellationToken cancellationToken)
        {
            var keyColumns = key.Columns();
            var data = new Dictionary<string, object?>(keyColumns, StringComparer.OrdinalIgnoreCase)
            {
                [Col.Result] = resultJson,
            };
            var tableRef = dialect.TableReference(store.TableSchema, store.DbName);
            var sql = MutationCommandExecutor.BuildUpdateSql(dialect, store, tableRef, new[] { Col.Result }, keyColumns, "");
            var affected = await MutationCommandExecutor.ExecuteNonQuery(connection, transaction, sql, data, cancellationToken: cancellationToken);
            if (affected != 1)
                throw new BifrostExecutionError("The operation record could not be completed, so the write was rolled back.");
        }
    }

    /// <summary>
    /// What one store record is keyed by: the owner (tenant and caller), the caller's operation
    /// id, and the response name of the mutation field it records.
    /// </summary>
    internal readonly record struct OperationRecordKey(OperationOwner Owner, string OperationId, string Field)
    {
        /// <summary>The key as store column values, in a fresh dictionary the caller may extend.</summary>
        public Dictionary<string, object?> Columns() => new(StringComparer.OrdinalIgnoreCase)
        {
            [Col.Tenant] = Owner.Tenant,
            [Col.Caller] = Owner.Caller,
            [Col.OperationId] = OperationId,
            [Col.Field] = Field,
        };
    }

    /// <summary>A committed operation record: the request it was recorded for and the field's result as JSON.</summary>
    internal sealed record OperationRecord(OperationFingerprint Fingerprint, string ResultJson);
}
