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

    /// <inheritdoc />
    public async Task<string[]> ListDatabasesAsync(CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString) { Database = "postgres" };
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname";

        var databases = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            databases.Add(reader.GetString(0));
        return databases.ToArray();
    }
}
