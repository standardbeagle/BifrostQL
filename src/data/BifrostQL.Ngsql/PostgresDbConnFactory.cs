using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using Npgsql;

namespace BifrostQL.Ngsql;

/// <summary>
/// PostgreSQL connection factory using Npgsql.
/// Provides the PostgreSQL dialect, schema reader, and type mapper.
/// </summary>
public sealed class PostgresDbConnFactory : IDbConnFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// Creates a new PostgreSQL connection factory.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string (e.g., "Host=localhost;Database=mydb;Username=postgres;Password=xxx").</param>
    public PostgresDbConnFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public ISqlDialect Dialect => PostgresDialect.Instance;

    /// <inheritdoc />
    public ISchemaReader SchemaReader => new PostgresSchemaReader();

    /// <inheritdoc />
    public ITypeMapper TypeMapper => PostgresTypeMapper.Instance;

    /// <inheritdoc />
    public DbConnection GetConnection()
    {
        return new NpgsqlConnection(_connectionString);
    }
}
