using System.Data;
using System.Data.Common;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// The transaction client-authored SQL (<c>_rawQuery</c>) runs in, opened by
/// <see cref="ISqlDialect.BeginReadOnlyScopeAsync"/>. Disposing it always rolls the
/// transaction back, so nothing the text did survives even on an engine without a
/// read-only transaction, then runs the dialect's connection reset (SQLite turns
/// <c>query_only</c> back off before the connection returns to the pool).
/// </summary>
public sealed class ReadOnlySqlScope : IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly Func<DbConnection, Task>? _resetConnection;

    public ReadOnlySqlScope(DbTransaction transaction, Func<DbConnection, Task>? resetConnection = null)
    {
        Transaction = transaction;
        _connection = transaction.Connection
            ?? throw new ArgumentException("The read-only transaction has no connection.", nameof(transaction));
        _resetConnection = resetConnection;
    }

    /// <summary>The transaction every command in the scope must enlist in.</summary>
    public DbTransaction Transaction { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // A transaction the server already ended (SqlClient clears Connection on a
            // zombied transaction) has nothing left to roll back; rolling back again would
            // replace the query's own error with "transaction has completed".
            if (Transaction.Connection is { State: ConnectionState.Open })
                await Transaction.RollbackAsync();
        }
        finally
        {
            await Transaction.DisposeAsync();
            if (_resetConnection != null && _connection.State == ConnectionState.Open)
                await _resetConnection(_connection);
        }
    }
}
