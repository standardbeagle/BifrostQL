using System.Data;
using System.Data.Common;
using BifrostQL.Core.Model;

namespace BifrostQL.Ngsql;

/// <summary>
/// PostgreSQL implementation of schema reader. Tables and columns come from information_schema;
/// primary keys, foreign keys, and unique and check constraints come from pg_constraint, because
/// information_schema.constraint_column_usage hides every row from a login that does not own the
/// table, which leaves an application login with a model that has no keys.
/// Excludes pg_catalog, information_schema, and ag_catalog (Apache AGE extension) system schemas.
/// Identity columns are detected by checking if column_default starts with 'nextval('.
/// </summary>
public sealed class PostgresSchemaReader : ISchemaReader
{
    // pg_constraint is readable by every login, so the reader applies information_schema.columns'
    // own visibility rule. A constraint counts only when the login can see all of its columns:
    // part of a composite key would widen a WHERE from one row to every row sharing that part,
    // and a foreign key to a hidden table would link to a table outside the model.
    private const string ConstraintIsVisible = @"
  AND NOT EXISTS (
      SELECT 1 FROM unnest(c.conkey) AS own(attnum)
      WHERE NOT (pg_has_role(constrained.relowner, 'USAGE')
          OR has_column_privilege(c.conrelid, own.attnum, 'SELECT, INSERT, UPDATE, REFERENCES')))
  AND (c.contype <> 'f' OR NOT EXISTS (
      SELECT 1 FROM unnest(c.confkey) AS referenced(attnum)
      WHERE NOT (pg_has_role(parent.relowner, 'USAGE')
          OR has_column_privilege(c.confrelid, referenced.attnum, 'SELECT, INSERT, UPDATE, REFERENCES'))))";

    private const string SchemaSql = @"
-- Constraints, keyed the way information_schema.constraint_column_usage keys them: a foreign
-- key lists the columns it references, every other kind its own, and a not-null constraint
-- (catalogued since PostgreSQL 18) reads as a check. A table's primary key comes first, each
-- constraint's columns in conkey order.
(SELECT
    current_database() AS table_catalog,
    tn.nspname AS table_schema,
    t.relname AS table_name,
    a.attname AS column_name,
    current_database() AS constraint_catalog,
    cn.nspname AS constraint_schema,
    c.conname AS constraint_name,
    CASE c.contype
        WHEN 'p' THEN 'PRIMARY KEY'
        WHEN 'u' THEN 'UNIQUE'
        WHEN 'f' THEN 'FOREIGN KEY'
        ELSE 'CHECK'
    END AS constraint_type
FROM pg_constraint c
JOIN pg_namespace cn ON cn.oid = c.connamespace
JOIN pg_class constrained ON constrained.oid = c.conrelid
LEFT JOIN pg_class parent ON parent.oid = c.confrelid
JOIN pg_class t ON t.oid = CASE c.contype WHEN 'f' THEN c.confrelid ELSE c.conrelid END
JOIN pg_namespace tn ON tn.oid = t.relnamespace
JOIN LATERAL unnest(CASE c.contype WHEN 'f' THEN c.confkey ELSE c.conkey END)
    WITH ORDINALITY AS k(attnum, ord) ON true
JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum AND NOT a.attisdropped
WHERE c.contype IN ('p', 'u', 'f', 'c', 'n')
  AND t.relkind IN ('r', 'p')
  AND NOT pg_is_other_temp_schema(tn.oid)
  AND tn.nspname NOT IN ('pg_catalog', 'information_schema', 'ag_catalog')" + ConstraintIsVisible + @"
ORDER BY tn.nspname, t.relname, c.contype = 'p' DESC, c.conname, k.ord)
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
    c.udt_name,
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
    END AS is_identity,
    CASE WHEN c.is_generated = 'ALWAYS' THEN 1 ELSE 0 END AS is_computed,
    -- An identity column reports a NULL column_default but the server still fills it.
    CASE
        WHEN c.column_default IS NOT NULL THEN 1
        WHEN c.identity_generation IS NOT NULL THEN 1
        ELSE 0
    END AS has_default
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

-- Foreign keys (child -> parent column pairs, ordered for composite keys). Only one whose target
-- is a declared key: information_schema has no constraint to report for a plain unique index.
SELECT
    c.conname          AS constraint_name,
    cns.nspname        AS child_schema,
    constrained.relname AS child_table,
    ca.attname         AS child_column,
    pns.nspname        AS parent_schema,
    parent.relname     AS parent_table,
    pa.attname         AS parent_column,
    k.ord              AS ordinal_position
FROM pg_constraint c
JOIN pg_class constrained ON constrained.oid = c.conrelid
JOIN pg_namespace cns ON cns.oid = constrained.relnamespace
JOIN pg_class parent ON parent.oid = c.confrelid
JOIN pg_namespace pns ON pns.oid = parent.relnamespace
JOIN LATERAL unnest(c.conkey, c.confkey) WITH ORDINALITY AS k(child_attnum, parent_attnum, ord) ON true
JOIN pg_attribute ca ON ca.attrelid = c.conrelid AND ca.attnum = k.child_attnum
JOIN pg_attribute pa ON pa.attrelid = c.confrelid AND pa.attnum = k.parent_attnum
WHERE c.contype = 'f'
  AND constrained.relkind IN ('r', 'p')
  AND NOT pg_is_other_temp_schema(cns.oid)
  AND cns.nspname NOT IN ('pg_catalog', 'information_schema', 'ag_catalog')
  AND EXISTS (
      SELECT 1 FROM pg_constraint target
      WHERE target.conrelid = c.confrelid AND target.conindid = c.conindid AND target.contype IN ('p', 'u'))" + ConstraintIsVisible + @"
ORDER BY cns.nspname, constrained.relname, c.conname, k.ord;

-- Indexes (key columns in key order). Expression keys (attnum 0) and partial
-- indexes are excluded — neither can serve an arbitrary client ORDER BY.
-- indisclustered reflects the last CLUSTER command; invalid indexes (failed
-- CREATE INDEX CONCURRENTLY) cannot serve queries and are excluded.
SELECT
    ns.nspname          AS table_schema,
    t.relname           AS table_name,
    ci.relname          AS index_name,
    ix.indisunique      AS is_unique,
    ix.indisclustered   AS is_clustered,
    ix.indisprimary     AS is_primary_key,
    a.attname           AS column_name,
    k.ord               AS key_ordinal
FROM pg_index ix
JOIN pg_class ci ON ci.oid = ix.indexrelid
JOIN pg_class t  ON t.oid = ix.indrelid
JOIN pg_namespace ns ON ns.oid = t.relnamespace
JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord)
    ON k.ord <= ix.indnkeyatts
JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
WHERE ns.nspname NOT IN ('pg_catalog', 'information_schema', 'ag_catalog')
  AND ix.indisvalid
  AND ix.indpred IS NULL
  AND NOT EXISTS (
      SELECT 1 FROM unnest(ix.indkey) WITH ORDINALITY AS ek(attnum, ord)
      WHERE ek.ord <= ix.indnkeyatts AND ek.attnum = 0)
ORDER BY ns.nspname, t.relname, ci.relname, k.ord;
";

    /// <inheritdoc />
    public async Task<SchemaData> ReadSchemaAsync(DbConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = SchemaSql;

        await using var reader = await cmd.ExecuteReaderAsync();

        var columnConstraints = (await ReadDtosAsync(reader, ColumnConstraintDto.FromReader))
            .GroupBy(k => new ColumnRef(k.TableCatalog, k.TableSchema, k.TableName, k.ColumnName))
            .ToDictionary(g => g.Key, g => g.ToList());

        await reader.NextResultAsync();

        var rawColumns = (await ReadDtosAsync(reader, r => ColumnDto.FromReader(r, columnConstraints))).ToArray();
        var columns = rawColumns
            .GroupBy(c => new TableRef(c.TableCatalog, c.TableSchema, c.TableName))
            .ToDictionary(g => g.Key, g => ColumnDto.DeduplicateGraphQlNames(g).ToArray());

        await reader.NextResultAsync();

        var tables = (await ReadDtosAsync(reader, r => DbTable.FromReader(
                r,
                columns[new TableRef((string)reader["table_catalog"], (string)reader["table_schema"], (string)reader["table_name"])])))
            .ToList();

        await reader.NextResultAsync();

        var foreignKeys = await ReadForeignKeysAsync(reader);

        await reader.NextResultAsync();

        var indexes = await ReadIndexesAsync(reader);

        return new SchemaData(columnConstraints, rawColumns, tables.Cast<IDbTable>().ToList(), foreignKeys, indexes);
    }

    private static async Task<IReadOnlyList<DbIndex>> ReadIndexesAsync(DbDataReader reader)
    {
        var rows = new List<(string Schema, string Table, string Name, bool IsUnique, bool IsClustered, bool IsPrimaryKey, string Column)>();
        while (await reader.ReadAsync())
        {
            rows.Add((
                (string)reader["table_schema"],
                (string)reader["table_name"],
                (string)reader["index_name"],
                (bool)reader["is_unique"],
                (bool)reader["is_clustered"],
                (bool)reader["is_primary_key"],
                (string)reader["column_name"]));
        }
        return rows
            .GroupBy(r => (r.Schema, r.Table, r.Name))
            .Select(g => new DbIndex
            {
                Name = g.Key.Name,
                TableSchema = g.Key.Schema,
                TableName = g.Key.Table,
                IsUnique = g.First().IsUnique,
                IsClustered = g.First().IsClustered,
                IsPrimaryKey = g.First().IsPrimaryKey,
                ColumnNames = g.Select(r => r.Column).ToArray(),
            })
            .ToList();
    }

    private static async Task<IReadOnlyList<DbForeignKey>> ReadForeignKeysAsync(DbDataReader reader)
    {
        var rows = new List<(string ConstraintName, string ChildSchema, string ChildTable, string ChildCol,
            string ParentSchema, string ParentTable, string ParentCol)>();
        while (await reader.ReadAsync())
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

    private static async Task<List<T>> ReadDtosAsync<T>(DbDataReader reader, Func<IDataReader, T> getDto)
    {
        var list = new List<T>();
        while (await reader.ReadAsync())
        {
            list.Add(getDto(reader));
        }
        return list;
    }
}
