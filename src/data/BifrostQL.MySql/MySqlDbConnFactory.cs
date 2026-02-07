using BifrostQL.Core.QueryModel;
using MySqlConnector;

namespace BifrostQL.MySql;

/// <summary>
/// MySQL/MariaDB connection factory using MySqlConnector.
/// Note: The current <c>IDbConnFactory</c> interface in BifrostQL.Core returns
/// <c>SqlConnection</c> (SQL Server specific). This factory provides MySQL
/// connections and the MySQL dialect, but cannot implement <c>IDbConnFactory</c>
/// until the interface is generalized to return <c>DbConnection</c>.
/// </summary>
public sealed class MySqlDbConnFactory
{
    private readonly string _connectionString;

    public MySqlDbConnFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public ISqlDialect Dialect => MySqlDialect.Instance;

    public MySqlConnection GetConnection()
    {
        return new MySqlConnection(_connectionString);
    }
}
