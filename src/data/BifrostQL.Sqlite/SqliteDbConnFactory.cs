using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Sqlite;

/// <summary>
/// SQLite connection factory using Microsoft.Data.Sqlite.
/// Provides the SQLite dialect, schema reader, and type mapper.
/// </summary>
public sealed class SqliteDbConnFactory : IDbConnFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// Creates a new SQLite connection factory.
    /// </summary>
    /// <param name="connectionString">SQLite connection string (e.g., "Data Source=mydb.db").</param>
    public SqliteDbConnFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public ISqlDialect Dialect => SqliteDialect.Instance;

    /// <inheritdoc />
    public ISchemaReader SchemaReader => new SqliteSchemaReader();

    /// <inheritdoc />
    public ITypeMapper TypeMapper => SqliteTypeMapper.Instance;

    /// <inheritdoc />
    public DbConnection GetConnection()
    {
        return new SqliteConnection(_connectionString);
    }
}
