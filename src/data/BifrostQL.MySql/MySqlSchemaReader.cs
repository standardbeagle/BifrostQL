using System.Data;
using System.Data.Common;
using BifrostQL.Core.Model;

namespace BifrostQL.MySql;

/// <summary>
/// MySQL/MariaDB implementation of schema reader using information_schema views.
/// Uses key_column_usage instead of constraint_column_usage (which MySQL lacks).
/// Scopes queries to the current database via DATABASE() function.
/// Identity columns are detected via the 'auto_increment' extra flag.
/// </summary>
public sealed class MySqlSchemaReader : ISchemaReader
{
    private const string SchemaSql = @"
-- Constraints
SELECT
    ccu.table_schema AS table_catalog,
    ccu.table_schema,
    ccu.table_name,
    ccu.column_name,
    ccu.constraint_schema AS constraint_catalog,
    ccu.constraint_schema,
    ccu.constraint_name,
    tc.constraint_type
FROM information_schema.key_column_usage ccu
INNER JOIN information_schema.table_constraints tc ON
    tc.constraint_schema = ccu.constraint_schema AND
    tc.constraint_name = ccu.constraint_name AND
    tc.table_name = ccu.table_name
WHERE ccu.table_schema = DATABASE()
UNION ALL
-- Unique constraints from unique indexes
SELECT
    DATABASE() AS table_catalog,
    DATABASE() AS table_schema,
    t.table_name,
    c.column_name,
    DATABASE() AS constraint_catalog,
    DATABASE() AS constraint_schema,
    t.index_name AS constraint_name,
    'UNIQUE' AS constraint_type
FROM information_schema.statistics t
JOIN information_schema.columns c ON 
    t.table_schema = c.table_schema AND
    t.table_name = c.table_name AND
    t.column_name = c.column_name
WHERE t.table_schema = DATABASE()
  AND t.non_unique = 0
  AND t.index_name != 'PRIMARY'
  AND t.seq_in_index = 1;

-- Columns
SELECT
    c.table_schema AS table_catalog,
    c.table_schema,
    c.table_name,
    c.column_name,
    c.ordinal_position,
    c.column_default,
    c.is_nullable,
    c.data_type,
    c.character_maximum_length,
    c.character_octet_length,
    c.numeric_precision,
    10 AS numeric_precision_radix,
    c.numeric_scale,
    c.datetime_precision,
    NULL AS character_set_catalog,
    NULL AS character_set_schema,
    c.character_set_name,
    NULL AS collation_catalog,
    NULL AS collation_schema,
    c.collation_name,
    NULL AS domain_catalog,
    NULL AS domain_schema,
    NULL AS domain_name,
    CASE WHEN c.extra = 'auto_increment' THEN 1 ELSE 0 END AS is_identity
FROM information_schema.columns c
WHERE c.table_schema = DATABASE()
ORDER BY c.table_schema, c.table_name, c.ordinal_position;

-- Tables
SELECT
    table_schema AS table_catalog,
    table_schema,
    table_name,
    table_type
FROM information_schema.tables
WHERE table_schema = DATABASE()
ORDER BY table_schema, table_name;
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
                columns[new TableRef((string)reader["table_catalog"], (string)reader["table_schema"], (string)reader["table_name"])]))
            .ToList();

        return new SchemaData(columnConstraints, rawColumns, tables.Cast<IDbTable>().ToList());
    }

    private static IEnumerable<T> GetDtos<T>(IDataReader reader, Func<IDataReader, T> getDto)
    {
        while (reader.Read())
        {
            yield return getDto(reader);
        }
    }
}
