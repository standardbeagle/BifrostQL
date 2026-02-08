using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using Npgsql;

namespace BifrostQL.Ngsql;

/// <summary>
/// PostgreSQL connection factory using Npgsql.
/// </summary>
public sealed class PostgresDbConnFactory : IDbConnFactory
{
    private readonly string _connectionString;

    public PostgresDbConnFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public ISqlDialect Dialect => PostgresDialect.Instance;

    public DbConnection GetConnection()
    {
        return new NpgsqlConnection(_connectionString);
    }
}
