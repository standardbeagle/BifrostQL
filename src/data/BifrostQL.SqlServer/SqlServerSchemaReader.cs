using System.Data;
using System.Data.Common;
using BifrostQL.Core.Model;

namespace BifrostQL.SqlServer;

/// <summary>
/// SQL Server implementation of schema reader using INFORMATION_SCHEMA views.
/// Queries CONSTRAINT_COLUMN_USAGE, COLUMNS, and TABLES views in a single batch.
/// Identity columns are detected via COLUMNPROPERTY(..., 'IsIdentity').
/// </summary>
public sealed class SqlServerSchemaReader : ISchemaReader
{
    private const string SchemaSql = @"
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
	TC.CONSTRAINT_NAME = CCU.CONSTRAINT_NAME
UNION ALL
-- Unique constraints from unique indexes
SELECT 
    DB_NAME() AS TABLE_CATALOG,
    SCHEMA_NAME(t.schema_id) AS TABLE_SCHEMA,
    t.name AS TABLE_NAME,
    c.name AS COLUMN_NAME,
    DB_NAME() AS CONSTRAINT_CATALOG,
    SCHEMA_NAME(t.schema_id) AS CONSTRAINT_SCHEMA,
    i.name AS CONSTRAINT_NAME,
    'UNIQUE' AS CONSTRAINT_TYPE
FROM sys.indexes i
INNER JOIN sys.tables t ON i.object_id = t.object_id
INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
WHERE i.is_unique = 1
  AND i.is_primary_key = 0
  AND t.is_ms_shipped = 0;
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

-- Foreign keys (child -> parent column pairs, ordered for composite keys).
-- Uses sys.foreign_keys + sys.foreign_key_columns because the
-- INFORMATION_SCHEMA REFERENTIAL_CONSTRAINTS join cannot reliably resolve
-- the referenced column on composite keys.
SELECT
    fk.name                       AS constraint_name,
    SCHEMA_NAME(fk.schema_id)     AS child_schema,
    OBJECT_NAME(fkc.parent_object_id)     AS child_table,
    cc.name                       AS child_column,
    SCHEMA_NAME(rt.schema_id)     AS parent_schema,
    OBJECT_NAME(fkc.referenced_object_id) AS parent_table,
    pc.name                       AS parent_column,
    fkc.constraint_column_id      AS ordinal_position
FROM sys.foreign_keys fk
INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
INNER JOIN sys.columns cc ON cc.object_id = fkc.parent_object_id     AND cc.column_id = fkc.parent_column_id
INNER JOIN sys.columns pc ON pc.object_id = fkc.referenced_object_id AND pc.column_id = fkc.referenced_column_id
INNER JOIN sys.tables  rt ON rt.object_id = fkc.referenced_object_id
ORDER BY SCHEMA_NAME(fk.schema_id), OBJECT_NAME(fkc.parent_object_id), fk.name, fkc.constraint_column_id;
";

    /// <inheritdoc />
    public async Task<SchemaData> ReadSchemaAsync(DbConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = SchemaSql;

        await using var reader = await cmd.ExecuteReaderAsync();

        var columnConstraints = GetDtos(reader, ColumnConstraintDto.FromReader)
            .GroupBy(k => new ColumnRef(k.TableCatalog, k.TableSchema, k.TableName, k.ColumnName))
            .ToDictionary(g => g.Key, g => g.ToList());

        await reader.NextResultAsync();

        var rawColumns = GetDtos(reader, r => ColumnDto.FromReader(r, columnConstraints)).ToArray();
        var columns = rawColumns
            .GroupBy(c => new TableRef(c.TableCatalog, c.TableSchema, c.TableName))
            .ToDictionary(g => g.Key, g => ColumnDto.DeduplicateGraphQlNames(g).ToArray());

        await reader.NextResultAsync();

        var tables = GetDtos(reader, r => DbTable.FromReader(
                r,
                columns[new TableRef((string)reader["TABLE_CATALOG"], (string)reader["TABLE_SCHEMA"], (string)reader["TABLE_NAME"])]))
            .ToList();

        await reader.NextResultAsync();

        var foreignKeys = ReadForeignKeys(reader);

        return new SchemaData(columnConstraints, rawColumns, tables.Cast<IDbTable>().ToList(), foreignKeys);
    }

    private static IReadOnlyList<DbForeignKey> ReadForeignKeys(DbDataReader reader)
    {
        var rows = new List<(string ConstraintName, string ChildSchema, string ChildTable, string ChildCol,
            string ParentSchema, string ParentTable, string ParentCol)>();
        while (reader.Read())
        {
            rows.Add((
                (string)reader["constraint_name"],
                (string)reader["child_schema"],
                (string)reader["child_table"],
                (string)reader["child_column"],
                (string)reader["parent_schema"],
                (string)reader["parent_table"],
                (string)reader["parent_column"]));
        }
        return rows
            .GroupBy(r => (r.ChildSchema, r.ChildTable, r.ConstraintName))
            .Select(g => new DbForeignKey
            {
                ConstraintName = g.Key.ConstraintName,
                ChildTableSchema = g.Key.ChildSchema,
                ChildTableName = g.Key.ChildTable,
                ChildColumnNames = g.Select(r => r.ChildCol).ToArray(),
                ParentTableSchema = g.First().ParentSchema,
                ParentTableName = g.First().ParentTable,
                ParentColumnNames = g.Select(r => r.ParentCol).ToArray(),
            })
            .ToList();
    }

    private static IEnumerable<T> GetDtos<T>(IDataReader reader, Func<IDataReader, T> getDto)
    {
        while (reader.Read())
        {
            yield return getDto(reader);
        }
    }
}
