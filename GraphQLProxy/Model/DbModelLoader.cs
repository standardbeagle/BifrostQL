using System.Data;
using System.Data.SqlClient;

namespace GraphQLProxy.Model
{
    public sealed class DbModelLoader
    {
        private readonly string _connStr;
        public DbModelLoader(IConfiguration configuration)
        {
            _connStr = configuration.GetConnectionString("ConnStr");
        }

        public async Task<DbModel> LoadAsync()
        {
            using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();
            var cmd = new SqlCommand("SELECT * FROM INFORMATION_SCHEMA.COLUMNS;SELECT * FROM INFORMATION_SCHEMA.TABLES;", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            var columns = GetDtos<ColumnDto>(reader, r => ColumnDto.FromReader(r))
                .GroupBy(c => $"{c.TableSchema}.{c.TableName}")
                .ToDictionary(g => g.Key, g => g.ToArray());
            await reader.NextResultAsync();
            return
                new DbModel()
                {
                    Tables = GetDtos<TableDto>(reader, r => TableDto.FromReader(r, columns[$"{reader["TABLE_SCHEMA"]}.{reader["TABLE_NAME"]}"])).ToList()
                };

        }
        IEnumerable<T> GetDtos<T>(IDataReader reader, Func<IDataReader, T> getDto)
        {
            while (reader.Read())
            {
                yield return getDto(reader);
            }
        }
    }
}
