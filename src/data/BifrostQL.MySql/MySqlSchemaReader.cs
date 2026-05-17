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

-- Foreign keys (child -> parent column pairs, ordered for composite keys)
SELECT
    kcu.constraint_name,
    kcu.table_schema   AS child_schema,
    kcu.table_name     AS child_table,
    kcu.column_name    AS child_column,
    kcu.referenced_table_schema AS parent_schema,
    kcu.referenced_table_name   AS parent_table,
    kcu.referenced_column_name  AS parent_column,
    kcu.ordinal_position
FROM information_schema.key_column_usage kcu
WHERE kcu.table_schema = DATABASE()
  AND kcu.referenced_table_name IS NOT NULL
ORDER BY kcu.table_schema, kcu.table_name, kcu.constraint_name, kcu.ordinal_position;
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

        await reader.NextResultAsync();

        var foreignKeys = ReadForeignKeys(reader);

        return new SchemaData(columnConstraints, rawColumns, tables.Cast<IDbTable>().ToList(), foreignKeys);
    }

    private static IReadOnlyList<DbForeignKey> ReadForeignKeys(DbDataReader reader)
    {
        // Group adjacent FK rows by (schema, table, constraint name); the
        // SQL already orders by ordinal_position so columns line up across
        // the composite-key case.
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
