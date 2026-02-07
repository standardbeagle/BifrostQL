using BifrostQL.Core.QueryModel;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Sqlite;

/// <summary>
/// SQLite connection factory using Microsoft.Data.Sqlite.
/// Note: The current <c>IDbConnFactory</c> interface in BifrostQL.Core returns
/// <c>SqlConnection</c> (SQL Server specific). This factory provides SQLite
/// connections and the SQLite dialect, but cannot implement <c>IDbConnFactory</c>
/// until the interface is generalized to return <c>DbConnection</c>.
/// </summary>
public sealed class SqliteDbConnFactory
{
    private readonly string _connectionString;

    public SqliteDbConnFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public ISqlDialect Dialect => SqliteDialect.Instance;

    public SqliteConnection GetConnection()
    {
        return new SqliteConnection(_connectionString);
    }
}
