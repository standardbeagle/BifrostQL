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
    /// <remarks>
    /// SQLite ships with foreign-key enforcement OFF per connection; every other
    /// supported dialect enforces declared FKs unconditionally. Leaving the default
    /// would let writes silently orphan child rows that SQL Server/Postgres/MySQL
    /// reject, so connections default to <c>Foreign Keys=True</c> (the pragma
    /// Microsoft.Data.Sqlite issues on open). An explicit <c>Foreign Keys=...</c>
    /// value in the caller's connection string is respected.
    /// </remarks>
    public SqliteDbConnFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        builder.ForeignKeys ??= true;
        _connectionString = builder.ConnectionString;
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
