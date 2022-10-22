using System.Data.SqlClient;

namespace GraphQLProxy.Model
{
    public interface IDbConnFactory
    {
        SqlConnection GetConnection();
    }

    public class DbConnFactory : IDbConnFactory
    {
        private readonly string _connectionString;
        public DbConnFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        public SqlConnection GetConnection()
        {
            return new SqlConnection(_connectionString);
        }
    }
}
