using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using MySqlConnector;

namespace BifrostQL.MySql;

/// <summary>
/// MySQL/MariaDB connection factory using MySqlConnector.
/// Provides the MySQL dialect, schema reader, and type mapper.
/// </summary>
public sealed class MySqlDbConnFactory : IDbConnFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// Creates a new MySQL/MariaDB connection factory.
    /// </summary>
    /// <param name="connectionString">MySQL connection string (e.g., "Server=localhost;Database=mydb;Uid=root;Pwd=xxx").</param>
    public MySqlDbConnFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public ISqlDialect Dialect => MySqlDialect.Instance;

    /// <inheritdoc />
    public ISchemaReader SchemaReader => new MySqlSchemaReader();

    /// <inheritdoc />
    public ITypeMapper TypeMapper => MySqlTypeMapper.Instance;

    /// <inheritdoc />
    public DbConnection GetConnection()
    {
        return new MySqlConnection(_connectionString);
    }
}
