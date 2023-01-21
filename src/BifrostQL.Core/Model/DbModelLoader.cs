using Microsoft.Extensions.Configuration;
using Pluralize.NET.Core;
using System.Data;
using System.Data.SqlClient;

namespace BifrostQL.Model
{
    public sealed class DbModelLoader
    {
        private readonly string _connStr;
        private readonly TableMatcher _ignoreTables;
        private readonly TableMatcher _includeTables;

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
            _ignoreTables = new TableMatcher(GetTableMatch(configuration.GetSection("IgnoreTables")), false);
            _includeTables = new TableMatcher(GetTableMatch(configuration.GetSection("IncludeTables")), true);
        }

        public async Task<IDbModel> LoadAsync()
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
            var model = 
                new DbModel()
                {
                    Tables = GetDtos<TableDto>(reader, r => TableDto.FromReader(
                        r, 
                        columns[new TableRef((string)reader["TABLE_CATALOG"], (string)reader["TABLE_SCHEMA"], (string)reader["TABLE_NAME"])]))
                    .Where(t => _includeTables.Match(t) == true)
                    .Where(t => _ignoreTables.Match(t) == false)
                    .ToList()
                };
            var pluralizer = new Pluralizer();
            var singleTables = model.Tables
                                .Where(t => t.KeyColumns.Count() == 1)
                                .Select(t => (pluralizer.Singularize(t.TableName), t)).ToDictionary(t => t.Item1, t => t.Item2);
            var idMatches = model.Tables
                            .SelectMany(table => table.Columns.Select(column => (table, column)))
                            .Where(c => string.Equals(c.column.ColumnName, "id", StringComparison.InvariantCultureIgnoreCase) == false)
                            .Where(c => c.column.ColumnName.EndsWith("id", StringComparison.InvariantCultureIgnoreCase))
                            .Select(c => (stripped: c.column.ColumnName.Remove(c.column.ColumnName.Length - 2).Replace("_", ""), c.column, c.table))
                            .Where(c => string.Equals(c.stripped, c.table.TableName, StringComparison.InvariantCultureIgnoreCase) == false)
                            .Select(c => (single: pluralizer.Singularize(c.stripped), c.column, c.table))
                            .Where(c => string.Equals(c.single, pluralizer.Singularize(c.table.TableName), StringComparison.InvariantCultureIgnoreCase) == false)
                            .Where(c => singleTables.ContainsKey(c.single))
                            .Select(c => (c.single, c.column, c.table, parent: singleTables[c.single]))
                            .Where(c => c.column.DataType == c.parent.KeyColumns.First().DataType)
                            .ToArray();
            foreach (var idMatch in idMatches)
            {
                var plural = pluralizer.Pluralize(idMatch.table.TableName);
                idMatch.table.SingleLinks.Add(idMatch.single.Replace(" ", "__"), new TableLinkDto { Name = idMatch.single, ChildId = idMatch.column, ParentId = idMatch.parent.KeyColumns.First(), ChildTable = idMatch.table, ParentTable = idMatch.parent });
                idMatch.parent.MultiLinks.Add(plural.Replace(" ", "__"), new TableLinkDto { Name = plural, ChildId = idMatch.column, ParentId = idMatch.parent.KeyColumns.First(), ChildTable = idMatch.table, ParentTable = idMatch.parent });
            }
            return model;
        }
        IEnumerable<T> GetDtos<T>(IDataReader reader, Func<IDataReader, T> getDto)
        {
            while (reader.Read())
            {
                yield return getDto(reader);
            }
        }
        private static (string schema, string[] tables)[] GetTableMatch(IConfigurationSection section)
        {
            if (section == null)
                return Array.Empty<(string schema, string[] tables)>();
            return section.GetChildren().Select(c => (c.Key, c.GetChildren().Select(cc => cc.Value).ToArray())).ToArray();
        }
    }
}
