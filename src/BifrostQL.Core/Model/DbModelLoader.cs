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
        private readonly TableMatcher _ignoreTables = new TableMatcher(false);
        private readonly TableMatcher _includeTables = new TableMatcher(true);
        private readonly ColumnMatcher _createDateMatcher = new ColumnMatcher(false);
        private readonly ColumnMatcher _updateDateMatcher = new ColumnMatcher(false);
        private readonly ColumnMatcher _updateByMatcher = new ColumnMatcher(false);
        private readonly ColumnMatcher _createByMatcher = new ColumnMatcher(false);
        private readonly string? _userAuditKey;
        private readonly string? _auditTableName;

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
            var bifrostSection = configuration.GetSection("BifrostQL");
            if (bifrostSection.Exists())
            {
                _ignoreTables = TableMatcher.FromSection(bifrostSection.GetSection("IgnoreTables"), false);
                _includeTables = TableMatcher.FromSection(bifrostSection.GetSection("IncludeTables"), true);
                var audit = bifrostSection.GetSection("Audit");
                if (audit.Exists())
                {
                    _userAuditKey = audit.GetValue<string>("UserKey");
                    _auditTableName = audit.GetValue<string>("AuditTable");
                    _createDateMatcher = ColumnMatcher.FromSection(audit.GetSection("CreatedOn"), false);
                    _updateDateMatcher = ColumnMatcher.FromSection(audit.GetSection("UpdatedOn"), false);
                    _createByMatcher = ColumnMatcher.FromSection(audit.GetSection("CreatedBy"), false);
                    _updateByMatcher = ColumnMatcher.FromSection(audit.GetSection("UpdatedBy"), false);
                }
            }
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

            var rawColumns = GetDtos<ColumnDto>(reader, r => ColumnDto.FromReader(r, columnConstraints)).ToArray();
            foreach(var column in rawColumns)
            {
                column.IsCreatedOnColumn = _createDateMatcher.Match(column);
                column.IsUpdatedOnColumn = _updateDateMatcher.Match(column);
                column.IsCreatedByColumn = _createByMatcher.Match(column);
                column.IsUpdatedByColumn = _updateByMatcher.Match(column);
            }
            var columns = rawColumns
                .GroupBy(c => new TableRef(c.TableCatalog, c.TableSchema, c.TableName))
                .ToDictionary(g => g.Key, g => g.ToArray());
            await reader.NextResultAsync();
            var model = 
                new DbModel()
                {
                    AuditTableName = _auditTableName ?? "",
                    UserAuditKey = _userAuditKey ?? "",
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
                idMatch.parent.MultiLinks.Add(idMatch.table.GraphQlName, new TableLinkDto { Name = idMatch.table.DbName, ChildId = idMatch.column, ParentId = idMatch.parent.KeyColumns.First(), ChildTable = idMatch.table, ParentTable = idMatch.parent });
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
    }
}
