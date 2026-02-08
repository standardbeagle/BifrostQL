using System.Data.Common;
using Microsoft.Data.SqlClient;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Model
{
    public interface IDbConnFactory
    {
        DbConnection GetConnection();
        ISqlDialect Dialect { get; }
    }

    public class DbConnFactory : IDbConnFactory
    {
        private readonly string _connectionString;
        public DbConnFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        public ISqlDialect Dialect => SqlServerDialect.Instance;

        public DbConnection GetConnection()
        {
            return new SqlConnection(_connectionString);
        }
    }
}
