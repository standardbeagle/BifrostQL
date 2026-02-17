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
}
