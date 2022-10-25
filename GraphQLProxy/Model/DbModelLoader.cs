using System.Data;
using System.Data.SqlClient;

namespace GraphQLProxy.Model
{
    public sealed class DbModelLoader
    {
        private readonly string _connStr;

        private const string SCHEMA_SQL = @"
SELECT [TABLE_CATALOG]
      ,[TABLE_SCHEMA]
      ,[TABLE_NAME]
      ,[COLUMN_NAME]
      ,[ORDINAL_POSITION]
      ,[COLUMN_DEFAULT]
      ,[IS_NULLABLE]
      ,[DATA_TYPE]
      ,[CHARACTER_MAXIMUM_LENGTH]
      ,[CHARACTER_OCTET_LENGTH]
      ,[NUMERIC_PRECISION]
      ,[NUMERIC_PRECISION_RADIX]
      ,[NUMERIC_SCALE]
      ,[DATETIME_PRECISION]
      ,[CHARACTER_SET_CATALOG]
      ,[CHARACTER_SET_SCHEMA]
      ,[CHARACTER_SET_NAME]
      ,[COLLATION_CATALOG]
      ,[COLLATION_SCHEMA]
      ,[COLLATION_NAME]
      ,[DOMAIN_CATALOG]
      ,[DOMAIN_SCHEMA]
      ,[DOMAIN_NAME]
  FROM [INFORMATION_SCHEMA].[COLUMNS];
SELECT [TABLE_CATALOG]
      ,[TABLE_SCHEMA]
      ,[TABLE_NAME]
      ,[TABLE_TYPE]
  FROM [INFORMATION_SCHEMA].[TABLES];";
        public DbModelLoader(IConfiguration configuration)
        {
            _connStr = configuration.GetConnectionString("ConnStr");
        }

        public async Task<DbModel> LoadAsync()
        {
            using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();
            var cmd = new SqlCommand(SCHEMA_SQL, conn);
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
