using System.Data.Common;
using Microsoft.Data.SqlClient;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Factory interface that provides database-specific components for BifrostQL.
    /// Each database dialect (SQL Server, PostgreSQL, MySQL, SQLite) has its own
    /// implementation that wires together the connection, SQL dialect, schema reader,
    /// and type mapper for that database.
    /// </summary>
    public interface IDbConnFactory
    {
        /// <summary>
        /// Creates and returns a new, unopened database connection.
        /// The caller is responsible for opening and disposing the connection.
        /// </summary>
        /// <returns>A new <see cref="DbConnection"/> configured with the factory's connection string.</returns>
        DbConnection GetConnection();

        /// <summary>
        /// The SQL dialect for generating database-specific SQL syntax
        /// (identifier escaping, pagination, LIKE patterns).
        /// </summary>
        ISqlDialect Dialect { get; }

        /// <summary>
        /// The schema reader for loading table, column, and constraint metadata
        /// from the database's system catalog or information schema.
        /// </summary>
        ISchemaReader SchemaReader { get; }

        /// <summary>
        /// The type mapper for converting database-specific data types to GraphQL types.
        /// </summary>
        ITypeMapper TypeMapper { get; }
    }

    /// <summary>
    /// Default SQL Server connection factory in the Core package.
    /// For explicit dialect selection, prefer <see cref="BifrostQL.SqlServer.SqlServerDbConnFactory"/>
    /// from the BifrostQL.SqlServer package.
    /// </summary>
    public class DbConnFactory : IDbConnFactory
    {
        private readonly string _connectionString;

        /// <summary>
        /// Creates a new SQL Server connection factory.
        /// </summary>
        /// <param name="connectionString">SQL Server connection string.</param>
        public DbConnFactory(string connectionString)
        {
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
}
