using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Sqlite;

/// <summary>
/// SQLite connection factory using Microsoft.Data.Sqlite.
/// </summary>
public sealed class SqliteDbConnFactory : IDbConnFactory
{
    private readonly string _connectionString;

    public SqliteDbConnFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public ISqlDialect Dialect => SqliteDialect.Instance;
    public ISchemaReader SchemaReader => new SqliteSchemaReader();

    public DbConnection GetConnection()
    {
        return new SqliteConnection(_connectionString);
    }
}
