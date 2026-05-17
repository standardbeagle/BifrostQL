using System.Data;
using System.Data.Common;
using BifrostQL.Core.Model;

namespace BifrostQL.Ngsql;

/// <summary>
/// PostgreSQL implementation of schema reader using information_schema views.
/// Excludes pg_catalog, information_schema, and ag_catalog (Apache AGE extension) system schemas.
/// Identity columns are detected by checking if column_default starts with 'nextval('.
/// </summary>
public sealed class PostgresSchemaReader : ISchemaReader
{
    private const string SchemaSql = @"
-- Constraints
SELECT
    ccu.table_catalog,
    ccu.table_schema,
    ccu.table_name,
    ccu.column_name,
    ccu.constraint_catalog,
    ccu.constraint_schema,
    ccu.constraint_name,
    tc.constraint_type
FROM information_schema.constraint_column_usage ccu
INNER JOIN information_schema.table_constraints tc ON
    tc.constraint_catalog = ccu.constraint_catalog AND
    tc.constraint_schema = ccu.constraint_schema AND
    tc.constraint_name = ccu.constraint_name
WHERE ccu.table_schema NOT IN ('pg_catalog', 'information_schema', 'ag_catalog')
UNION ALL
-- Unique constraints from unique indexes
SELECT
    current_database() AS table_catalog,
    schemaname AS table_schema,
    tablename AS table_name,
    a.attname AS column_name,
    current_database() AS constraint_catalog,
    schemaname AS constraint_schema,
    indexname AS constraint_name,
    'UNIQUE' AS constraint_type
FROM pg_indexes pi
JOIN pg_class t ON t.relname = pi.tablename AND t.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = pi.schemaname)
JOIN pg_index ix ON ix.indexrelid = to_regclass(pi.schemaname || '.' || pi.indexname)
JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
WHERE ix.indisunique = true
  AND ix.indisprimary = false
  AND to_regclass(pi.schemaname || '.' || pi.indexname) IS NOT NULL
  AND schemaname NOT IN ('pg_catalog', 'information_schema', 'ag_catalog');

-- Columns
SELECT
    c.table_catalog,
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
    c.numeric_precision_radix,
    c.numeric_scale,
    c.datetime_precision,
    c.character_set_catalog,
    c.character_set_schema,
    c.character_set_name,
    c.collation_catalog,
    c.collation_schema,
    c.collation_name,
    c.domain_catalog,
    c.domain_schema,
    c.domain_name,
    CASE
        WHEN c.column_default LIKE 'nextval(%' THEN 1
        WHEN c.identity_generation IS NOT NULL THEN 1
        ELSE 0
    END AS is_identity
FROM information_schema.columns c
WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema', 'ag_catalog')
ORDER BY c.table_catalog, c.table_schema, c.table_name, c.ordinal_position;

-- Tables
SELECT
    table_catalog,
    table_schema,
    table_name,
    table_type
FROM information_schema.tables
WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'ag_catalog')
ORDER BY table_catalog, table_schema, table_name;

-- Foreign keys (child -> parent column pairs, ordered for composite keys)
SELECT
    kcu.constraint_name,
    kcu.table_schema   AS child_schema,
    kcu.table_name     AS child_table,
    kcu.column_name    AS child_column,
    ccu.table_schema   AS parent_schema,
    ccu.table_name     AS parent_table,
    ccu.column_name    AS parent_column,
    kcu.ordinal_position
FROM information_schema.table_constraints tc
INNER JOIN information_schema.key_column_usage kcu ON
    tc.constraint_catalog = kcu.constraint_catalog AND
    tc.constraint_schema  = kcu.constraint_schema  AND
    tc.constraint_name    = kcu.constraint_name
INNER JOIN information_schema.referential_constraints rc ON
    tc.constraint_catalog = rc.constraint_catalog AND
    tc.constraint_schema  = rc.constraint_schema  AND
    tc.constraint_name    = rc.constraint_name
INNER JOIN information_schema.key_column_usage ccu ON
    rc.unique_constraint_catalog = ccu.constraint_catalog AND
    rc.unique_constraint_schema  = ccu.constraint_schema  AND
    rc.unique_constraint_name    = ccu.constraint_name    AND
    kcu.position_in_unique_constraint = ccu.ordinal_position
WHERE tc.constraint_type = 'FOREIGN KEY'
  AND tc.table_schema NOT IN ('pg_catalog', 'information_schema', 'ag_catalog')
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
