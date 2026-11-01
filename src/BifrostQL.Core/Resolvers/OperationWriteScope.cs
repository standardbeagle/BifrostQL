using System.Data.Common;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// One connection and one transaction shared by every read and write a mutation
    /// operation makes (see <c>BifrostQL.Core.Operations.IBifrostOperation</c>). It is opened by
    /// <see cref="MutationCommandExecutor.RunInTransactionAsync(IDbConnFactory, Func{DbConnection, DbTransaction, Task}, CancellationToken)"/>,
    /// the same open/commit/rollback/error-mapping a generated mutation uses, so an operation
    /// commits as a unit or rolls back as a unit and its database errors reach the wire exactly
    /// as a generated write's do.
    ///
    /// <para>A pipeline write joins the scope instead of opening its own transaction
    /// (<see cref="MutationPipelineContext.WriteScope"/>), and an intent read runs on the
    /// scope's connection, so the operation reads its own uncommitted writes and never blocks
    /// on its own locks from a second connection. Post-commit notifications (mutation and
    /// state-transition observers) are queued with <see cref="AfterCommit"/> and run only once
    /// the scope has committed, so no observer sees a write that is later rolled back.</para>
    ///
    /// <para>A joined write that throws marks the scope faulted. The operation's code may catch
    /// the exception, but the scope still refuses to commit: on some engines the transaction is
    /// already unusable after a failed statement, and a half-applied operation is exactly what
    /// the scope exists to prevent.</para>
    /// </summary>
    internal sealed class OperationWriteScope
    {
        private readonly List<Func<Task>> _afterCommit = new();
        private bool _faulted;

        private OperationWriteScope(DbConnection connection, DbTransaction transaction)
        {
            Connection = connection;
            Transaction = transaction;
        }

        public DbConnection Connection { get; }

        public DbTransaction Transaction { get; }

        /// <summary>Queues a post-commit notification; it runs only if the scope commits.</summary>
        public void AfterCommit(Func<Task> notification) => _afterCommit.Add(notification);

        /// <summary>
        /// Runs <paramref name="body"/> inside a new scope, commits, then runs the queued
        /// post-commit notifications in order.
        /// </summary>
        public static async Task<T> RunAsync<T>(
            IDbConnFactory connFactory, Func<OperationWriteScope, Task<T>> body, CancellationToken cancellationToken)
        {
            OperationWriteScope? scope = null;
            T result = default!;
            await MutationCommandExecutor.RunInTransactionAsync(connFactory, async (connection, transaction) =>
            {
                scope = new OperationWriteScope(connection, transaction);
                result = await body(scope);
                if (scope._faulted)
                    throw new BifrostExecutionError(
                        "A write inside the operation failed, so the operation was rolled back.");
            }, cancellationToken);

            foreach (var notification in scope!._afterCommit)
                await notification();
            return result;
        }

        /// <summary>
        /// Runs one pipeline write on the scope's connection and transaction. Commit and
        /// rollback belong to <see cref="RunAsync{T}"/>; this only records a failure.
        /// </summary>
        public async Task JoinAsync(Func<DbConnection, DbTransaction, Task> work)
        {
            if (_faulted)
                throw new BifrostExecutionError(
                    "A previous write inside the operation failed; no further writes run in it.");
            try
            {
                await work(Connection, Transaction);
            }
            catch (Exception ex) when (ex is BifrostExecutionError or OperationCanceledException)
            {
                _faulted = true;
                throw;
            }
            catch (Exception ex)
            {
                // The same mapping a standalone write applies in RunInTransactionAsync.
                _faulted = true;
                throw BifrostExecutionError.FromDatabaseException(ex);
            }
        }
    }
}
