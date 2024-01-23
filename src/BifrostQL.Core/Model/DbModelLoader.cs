using BifrostQL.Core.Model;
using Microsoft.Extensions.Configuration;
using Pluralize.NET.Core;
using System.Data;
using System.Data.SqlClient;

namespace BifrostQL.Model
{
    public sealed class DbModelLoader
    {
        private readonly string _connStr;
        private readonly IMetadataLoader _metadataLoader;
        private readonly IConfigurationSection _configuration;

        private const string SCHEMA_SQL = @"
SELECT CCU.[TABLE_CATALOG]
      ,CCU.[TABLE_SCHEMA]
      ,CCU.[TABLE_NAME]
      ,CCU.[COLUMN_NAME]
      ,CCU.[CONSTRAINT_CATALOG]
      ,CCU.[CONSTRAINT_SCHEMA]
      ,CCU.[CONSTRAINT_NAME]
	  ,TC.[CONSTRAINT_TYPE]
  FROM [INFORMATION_SCHEMA].[CONSTRAINT_COLUMN_USAGE] CCU
  INNER JOIN [INFORMATION_SCHEMA].[TABLE_CONSTRAINTS] TC ON 
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
  FROM [INFORMATION_SCHEMA].[COLUMNS]
  ORDER BY [TABLE_CATALOG],[TABLE_SCHEMA],[TABLE_NAME],[ORDINAL_POSITION];
SELECT [TABLE_CATALOG]
      ,[TABLE_SCHEMA]
      ,[TABLE_NAME]
      ,[TABLE_TYPE]
  FROM [INFORMATION_SCHEMA].[TABLES]
  ORDER BY [TABLE_CATALOG],[TABLE_SCHEMA],[TABLE_NAME];
";
        public DbModelLoader(IConfigurationSection bifrostSection, string connectionString, IMetadataLoader metadataLoader)
        {
            _configuration = bifrostSection;
            _connStr = connectionString;
            _metadataLoader = metadataLoader;
        }

        public async Task<IDbModel> LoadAsync()
        {
            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();
            var cmd = new SqlCommand(SCHEMA_SQL, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            var columnConstraints = GetDtos(reader, ColumnConstraintDto.FromReader)
                .GroupBy(k => new ColumnRef(k.TableCatalog, k.TableSchema, k.TableName, k.ColumnName))
                .ToDictionary(g => g.Key, g => g.ToList());
            await reader.NextResultAsync();

            var rawColumns = GetDtos(reader, r => ColumnDto.FromReader(r, columnConstraints)).ToArray();
            var columns = rawColumns
                .GroupBy(c => new TableRef(c.TableCatalog, c.TableSchema, c.TableName))
                .ToDictionary(g => g.Key, g => g.ToArray());
            await reader.NextResultAsync();
            var tables = GetDtos(reader, r => DbTable.FromReader(
                    r,
                    columns[new TableRef((string)reader["TABLE_CATALOG"], (string)reader["TABLE_SCHEMA"], (string)reader["TABLE_NAME"])]))
                .ToList();
            foreach (var table in tables)
            {
                _metadataLoader.ApplyTableMetadata(table, table.Metadata);
                foreach (var column in table.Columns)
                {
                    _metadataLoader.ApplyColumnMetadata(table, column, column.Metadata);
                }
            }
            var dbMetadata = new Dictionary<string, object?>();
            _metadataLoader.ApplyDatabaseMetadata(dbMetadata);
            var model =
                new DbModel()
                {
                    Tables = tables.Where(t => t.CompareMetadata("visibility","hidden") == false).ToList(),
                    Metadata = dbMetadata,
                };
            var singleTables = model.Tables
                                .Where(t => t.KeyColumns.Count() == 1)
                                .ToDictionary(t => t.NormalizedName, StringComparer.InvariantCultureIgnoreCase);
            var idMatches = model.Tables
                            .SelectMany(table => table.Columns.Select(column => (table, column)))
                            .Where(c => singleTables.ContainsKey(c.column.NormalizedName))
                            .Where(c => string.Equals(c.column.NormalizedName, c.table.NormalizedName, StringComparison.InvariantCultureIgnoreCase) == false)
                            .Select(c => (c.column, c.table, parent: singleTables[c.column.NormalizedName]))
                            .Where(c => c.column.DataType == c.parent.KeyColumns.First().DataType)
                            .ToArray();
            foreach (var idMatch in idMatches)
            {
                idMatch.table.SingleLinks.Add(idMatch.parent.GraphQlName, new TableLinkDto { Name = idMatch.parent.GraphQlName, ChildId = idMatch.column, ParentId = idMatch.parent.KeyColumns.First(), ChildTable = idMatch.table, ParentTable = idMatch.parent });
                idMatch.parent.MultiLinks.Add(idMatch.table.GraphQlName, new TableLinkDto { Name = idMatch.table.GraphQlName, ChildId = idMatch.column, ParentId = idMatch.parent.KeyColumns.First(), ChildTable = idMatch.table, ParentTable = idMatch.parent });
            }

            foreach (var entry in _configuration.AsEnumerable())
            {
                if (entry.Value == null) continue;
                model.ConfigurationData[entry.Key] = entry.Value;
            }
            return model;
        }

        private static IEnumerable<T> GetDtos<T>(IDataReader reader, Func<IDataReader, T> getDto)
        {
            while (reader.Read())
            {
                yield return getDto(reader);
            }
        }
    }
}
