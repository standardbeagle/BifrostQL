using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using MySqlConnector;

namespace BifrostQL.MySql;

/// <summary>
/// MySQL/MariaDB connection factory using MySqlConnector.
/// </summary>
public sealed class MySqlDbConnFactory : IDbConnFactory
{
    private readonly string _connectionString;

    public MySqlDbConnFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public ISqlDialect Dialect => MySqlDialect.Instance;
    public ISchemaReader SchemaReader => new MySqlSchemaReader();

    public DbConnection GetConnection()
    {
        return new MySqlConnection(_connectionString);
    }
}
