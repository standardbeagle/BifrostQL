using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using Microsoft.Data.SqlClient;

namespace BifrostQL.SqlServer;

/// <summary>
/// SQL Server connection factory using Microsoft.Data.SqlClient.
/// Provides the SQL Server dialect, schema reader, and type mapper.
/// </summary>
public sealed class SqlServerDbConnFactory : IDbConnFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// Creates a new SQL Server connection factory.
    /// </summary>
    /// <param name="connectionString">SQL Server connection string (e.g., "Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True").</param>
    public SqlServerDbConnFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public ISqlDialect Dialect => SqlServerDialect.Instance;

    /// <inheritdoc />
    public ISchemaReader SchemaReader => new SqlServerSchemaReader();

    /// <inheritdoc />
    public ITypeMapper TypeMapper => SqlServerTypeMapper.Instance;

    /// <inheritdoc />
    public DbConnection GetConnection()
    {
        return new SqlConnection(_connectionString);
    }

    /// <inheritdoc />
    public async Task<string[]> ListDatabasesAsync(CancellationToken cancellationToken = default)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name";

        var databases = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            databases.Add(reader.GetString(0));
        return databases.ToArray();
    }
}
