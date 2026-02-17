using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using Microsoft.Data.SqlClient;

namespace BifrostQL.SqlServer;

/// <summary>
/// SQL Server connection factory using Microsoft.Data.SqlClient.
/// </summary>
public sealed class SqlServerDbConnFactory : IDbConnFactory
{
    private readonly string _connectionString;

    public SqlServerDbConnFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public ISqlDialect Dialect => SqlServerDialect.Instance;
    public ISchemaReader SchemaReader => new SqlServerSchemaReader();
    public ITypeMapper TypeMapper => SqlServerTypeMapper.Instance;

    public DbConnection GetConnection()
    {
        return new SqlConnection(_connectionString);
    }
}
