using BifrostQL.Core.QueryModel;
using Npgsql;

namespace BifrostQL.Ngsql;

/// <summary>
/// PostgreSQL connection factory using Npgsql.
/// Note: The current <c>IDbConnFactory</c> interface in BifrostQL.Core returns
/// <c>SqlConnection</c> (SQL Server specific). This factory provides PostgreSQL
/// connections and the PostgreSQL dialect, but cannot implement <c>IDbConnFactory</c>
/// until the interface is generalized to return <c>DbConnection</c>.
/// </summary>
public sealed class PostgresDbConnFactory
{
    private readonly string _connectionString;

    public PostgresDbConnFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public ISqlDialect Dialect => PostgresDialect.Instance;

    public NpgsqlConnection GetConnection()
    {
        return new NpgsqlConnection(_connectionString);
    }
}
