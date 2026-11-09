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
    /// <para>Any statement in the scope that fails, read or write, marks the scope faulted. The
    /// operation's code may catch the exception, but the scope refuses every later statement and
    /// refuses to commit, on every engine: Postgres has already aborted the transaction and would
    /// answer COMMIT with a silent rollback, the others would commit half an operation, and a
    /// half-applied operation is exactly what the scope exists to prevent. A rule refused before
    /// any statement ran (no row to lock, a column the caller may not read) is not a failed
    /// statement and does not fault the scope.</para>
    /// </summary>
    internal sealed class OperationWriteScope
    {
        private readonly List<Func<Task>> _afterCommit = new();
        private Exception? _firstFault;

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
                if (scope._firstFault is not null)
                    throw scope.Refusal("A statement inside the operation failed, so the operation was rolled back.");
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
            ThrowIfFaulted();
            try
            {
                await work(Connection, Transaction);
            }
            catch (Exception ex) when (ex is BifrostExecutionError or OperationCanceledException)
            {
                MarkFaulted(ex);
                throw;
            }
            catch (Exception ex)
            {
                // The same mapping a standalone write applies in RunInTransactionAsync.
                var error = BifrostExecutionError.FromDatabaseException(ex);
                MarkFaulted(error);
                throw error;
            }
        }

        /// <summary>Refuses a statement once an earlier one in the scope has failed.</summary>
        public void ThrowIfFaulted()
        {
            if (_firstFault is not null)
                throw Refusal("A previous statement inside the operation failed; no further statements run in it.");
        }

        /// <summary>Records a failed statement; only the first failure names the operation's error.</summary>
        public void MarkFaulted(Exception fault) => _firstFault ??= fault;

        private BifrostExecutionError Refusal(string message) =>
            new(message, _firstFault!) { ErrorCode = (_firstFault as BifrostExecutionError)?.ErrorCode };
    }
}
