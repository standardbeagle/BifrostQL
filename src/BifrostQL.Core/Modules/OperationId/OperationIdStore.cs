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
        /// Inserts the record for <paramref name="operationId"/> before the write runs, with no
        /// result yet. The store's unique key over (tenant, caller, operation id) makes a second
        /// transaction claiming the same id wait for the first and then fail, so one id is one
        /// effect.
        /// </summary>
        public static async Task ClaimAsync(
            IDbTable store, ISqlDialect dialect, DbConnection connection, DbTransaction transaction,
            OperationOwner owner, string operationId, OperationFingerprint fingerprint, CancellationToken cancellationToken)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [Col.Tenant] = owner.Tenant,
                [Col.Caller] = owner.Caller,
                [Col.OperationId] = operationId,
                [Col.Table] = fingerprint.Table,
                [Col.Action] = fingerprint.Action,
                [Col.TargetKey] = fingerprint.TargetKey,
                [Col.InputHash] = fingerprint.InputHash,
                [Col.CreatedAt] = DateTime.UtcNow,
            };
            var tableRef = dialect.TableReference(store.TableSchema, store.DbName);
            var sql = MutationCommandExecutor.BuildInsertInto(dialect, store, tableRef, row.Keys) + ";";
            await MutationCommandExecutor.ExecuteNonQuery(connection, transaction, sql, row, cancellationToken: cancellationToken);
        }

        /// <summary>Writes the mutation field's result onto the claimed record.</summary>
        public static async Task RecordResultAsync(
            IDbTable store, ISqlDialect dialect, DbConnection connection, DbTransaction transaction,
            OperationOwner owner, string operationId, string resultJson, CancellationToken cancellationToken)
        {
            var key = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [Col.Tenant] = owner.Tenant,
                [Col.Caller] = owner.Caller,
                [Col.OperationId] = operationId,
            };
            var data = new Dictionary<string, object?>(key, StringComparer.OrdinalIgnoreCase)
            {
                [Col.Result] = resultJson,
            };
            var tableRef = dialect.TableReference(store.TableSchema, store.DbName);
            var sql = MutationCommandExecutor.BuildUpdateSql(dialect, store, tableRef, new[] { Col.Result }, key, "");
            var affected = await MutationCommandExecutor.ExecuteNonQuery(connection, transaction, sql, data, cancellationToken: cancellationToken);
            if (affected != 1)
                throw new BifrostExecutionError("The operation record could not be completed, so the write was rolled back.");
        }
    }
}
