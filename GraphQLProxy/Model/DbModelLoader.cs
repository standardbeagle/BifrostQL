﻿using System.Data;
using System.Data.SqlClient;

namespace GraphQLProxy.Model
{
    public sealed class DbModelLoader
    {
        private readonly string _connStr;

        private const string SCHEMA_SQL = @"
SELECT CCU.[TABLE_CATALOG]
      ,CCU.[TABLE_SCHEMA]
      ,CCU.[TABLE_NAME]
      ,CCU.[COLUMN_NAME]
      ,CCU.[CONSTRAINT_CATALOG]
      ,CCU.[CONSTRAINT_SCHEMA]
      ,CCU.[CONSTRAINT_NAME]
	  ,TC.[CONSTRAINT_TYPE]
  FROM [portal].[INFORMATION_SCHEMA].[CONSTRAINT_COLUMN_USAGE] CCU
  INNER JOIN [portal].[INFORMATION_SCHEMA].[TABLE_CONSTRAINTS] TC ON 
	TC.CONSTRAINT_CATALOG = CCU.CONSTRAINT_CATALOG AND
	TC.CONSTRAINT_SCHEMA = CCU.CONSTRAINT_SCHEMA AND
	TC.CONSTRAINT_NAME = CCU.CONSTRAINT_NAME;
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
      ,COLUMNPROPERTY (OBJECT_ID(TABLE_SCHEMA + '.' + TABLE_NAME),COLUMN_NAME ,'IsIdentity') [IS_IDENTITY]
  FROM [INFORMATION_SCHEMA].[COLUMNS];
SELECT [TABLE_CATALOG]
      ,[TABLE_SCHEMA]
      ,[TABLE_NAME]
      ,[TABLE_TYPE]
  FROM [INFORMATION_SCHEMA].[TABLES];
";
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
            var columnConstraints = GetDtos<ColumnConstraintDto>(reader, r => ColumnConstraintDto.FromReader(r))
                .GroupBy(k => new ColumnRef(k.TableCatalog, k.TableSchema, k.TableName, k.ColumnName))
                .ToDictionary(g => g.Key, g => g.ToList());
            await reader.NextResultAsync();
            var columns = GetDtos<ColumnDto>(reader, r => ColumnDto.FromReader(r, columnConstraints))
                .GroupBy(c => new TableRef(c.TableCatalog, c.TableSchema, c.TableName))
                .ToDictionary(g => g.Key, g => g.ToArray());
            await reader.NextResultAsync();
            return
                new DbModel()
                {
                    Tables = GetDtos<TableDto>(reader, r => TableDto.FromReader(
                        r, 
                        columns[new TableRef((string)reader["TABLE_CATALOG"], (string)reader["TABLE_SCHEMA"], (string)reader["TABLE_NAME"])]))
                    .Where(t => t.TableName.StartsWith("_") == false)
                    .ToList()
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
