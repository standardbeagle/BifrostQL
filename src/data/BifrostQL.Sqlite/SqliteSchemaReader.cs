using System.Data;
using System.Data.Common;
using BifrostQL.Core.Model;

namespace BifrostQL.Sqlite;

/// <summary>
/// SQLite implementation of schema reader using PRAGMA commands.
/// SQLite lacks information_schema, so table_info and foreign_key_list pragmas
/// are used per-table. Identity columns are detected as INTEGER PRIMARY KEY
/// (SQLite's rowid alias convention). All schema/catalog values use "main".
/// </summary>
public sealed class SqliteSchemaReader : ISchemaReader
{
    /// <inheritdoc />
    public async Task<SchemaData> ReadSchemaAsync(DbConnection connection)
    {
        var tables = new List<IDbTable>();
        var allColumns = new List<ColumnDto>();
        var columnConstraints = new Dictionary<ColumnRef, List<ColumnConstraintDto>>();
        var foreignKeys = new List<DbForeignKey>();
        var indexes = new List<DbIndex>();

        // Get all tables
        var tablesCmd = connection.CreateCommand();
        tablesCmd.CommandText = "SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name";

        await using var tablesReader = await tablesCmd.ExecuteReaderAsync();
        var tableNames = new List<(string name, string type)>();
        while (await tablesReader.ReadAsync())
        {
            tableNames.Add(((string)tablesReader["name"], (string)tablesReader["type"]));
        }

        // For each table, get column information and constraints
        foreach (var (tableName, tableType) in tableNames)
        {
            // First, collect all constraints for this table
            await CollectConstraintsAsync(connection, tableName, columnConstraints, foreignKeys);
            await CollectIndexesAsync(connection, tableName, indexes);

            // Then read columns (which will use the constraints)
            var columnsCmd = connection.CreateCommand();
            columnsCmd.CommandText = $"PRAGMA table_xinfo({tableName})";

            await using var colReader = await columnsCmd.ExecuteReaderAsync();
            var rawCols = new List<(string name, string type, bool notNull, bool isPk, bool isComputed)>();

            while (await colReader.ReadAsync())
            {
                var hidden = (long)colReader["hidden"];
                // hidden: 0=normal, 1=hidden rowid, 2=virtual generated, 3=stored generated
                if (hidden == 1)
                    continue;

                rawCols.Add((
                    (string)colReader["name"],
                    (string)colReader["type"],
                    ((long)colReader["notnull"]) == 1,
                    ((long)colReader["pk"]) > 0,
                    hidden is 2 or 3
                ));
            }

            // AUTOINCREMENT requires single INTEGER PRIMARY KEY - composite PKs cannot be identity
            var pkCount = rawCols.Count(c => c.isPk);
            var tableColumns = new List<ColumnDto>();
            var ordinal = 1;

            foreach (var (columnName, dataType, notNull, isPk, isComputed) in rawCols)
            {
                var isIdentity = pkCount == 1 && isPk && dataType.Equals("INTEGER", StringComparison.OrdinalIgnoreCase);
                var columnRef = new ColumnRef("main", "main", tableName, columnName);
                var isPrimary = columnConstraints.TryGetValue(columnRef, out var con) && con.Any(c => c.ConstraintType == "PRIMARY KEY");
                var isUnique = columnConstraints.TryGetValue(columnRef, out var uniqueCons) && uniqueCons.Any(c => c.ConstraintType == "UNIQUE");

                // SQLite has no INFORMATION_SCHEMA; the declared type text
                // ("NVARCHAR(50)", "DECIMAL(10,2)") is the only source of
                // length/precision facts.
                var (precision, scale) = DeclaredTypeFacts.PrecisionScale(dataType);
                var column = new ColumnDto
                {
                    TableCatalog = "main",
                    TableSchema = "main",
                    TableName = tableName,
                    ColumnName = columnName,
                    GraphQlName = columnName.ToGraphQl("col"),
                    NormalizedName = ColumnDto.NormalizeColumn(columnName),
                    ColumnRef = columnRef,
                    DataType = dataType,
                    CharacterMaxLength = DeclaredTypeFacts.CharacterMaxLength(dataType),
                    NumericPrecision = precision,
                    NumericScale = scale,
                    IsNullable = isPk ? false : !notNull,
                    OrdinalPosition = ordinal++,
                    IsIdentity = isIdentity,
                    IsComputed = isComputed,
                    IsPrimaryKey = isPrimary,
                    IsUnique = isUnique,
                };

                tableColumns.Add(column);
                allColumns.Add(column);
            }

            var graphQlName = tableName.ToGraphQl("tbl");
            var dbTable = new DbTable
            {
                DbName = tableName,
                GraphQlName = graphQlName,
                NormalizedName = new Pluralize.NET.Core.Pluralizer().Singularize(tableName),
                TableSchema = "main",
                TableType = tableType == "view" ? "VIEW" : "BASE TABLE",
                ColumnLookup = tableColumns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase),
                GraphQlLookup = tableColumns.ToDictionary(c => c.GraphQlName, StringComparer.OrdinalIgnoreCase),
            };

            tables.Add(dbTable);
        }

        return new SchemaData(columnConstraints, allColumns.ToArray(), tables, foreignKeys, indexes);
    }

    /// <summary>
    /// Collects every index on <paramref name="tableName"/> (unique and not).
    /// Partial indexes are skipped — they cover a subset of rows, so they cannot
    /// serve an arbitrary ORDER BY. Expression-key indexes are skipped for the
    /// same reason: index_info reports a NULL column name for expression keys,
    /// and there is no column a client could sort by. SQLite rowid tables are
    /// physically ordered by rowid, so no index is marked clustered.
    /// </summary>
    private static async Task CollectIndexesAsync(DbConnection connection, string tableName, List<DbIndex> indexes)
    {
        var listCmd = connection.CreateCommand();
        listCmd.CommandText = $"PRAGMA index_list({tableName})";

        var indexRows = new List<(string Name, bool IsUnique, string Origin)>();
        await using (var listReader = await listCmd.ExecuteReaderAsync())
        {
            while (await listReader.ReadAsync())
            {
                if ((long)listReader["partial"] == 1)
                    continue;
                indexRows.Add((
                    (string)listReader["name"],
                    (long)listReader["unique"] == 1,
                    (string)listReader["origin"]));
            }
        }

        foreach (var (indexName, isUnique, origin) in indexRows)
        {
            var infoCmd = connection.CreateCommand();
            infoCmd.CommandText = $"PRAGMA index_info({indexName})";

            var columnNames = new List<string>();
            var hasExpressionKey = false;
            await using (var infoReader = await infoCmd.ExecuteReaderAsync())
            {
                while (await infoReader.ReadAsync())
                {
                    if (infoReader["name"] is DBNull)
                    {
                        hasExpressionKey = true;
                        break;
                    }
                    columnNames.Add((string)infoReader["name"]);
                }
            }

            if (hasExpressionKey || columnNames.Count == 0)
                continue;

            indexes.Add(new DbIndex
            {
                Name = indexName,
                TableSchema = "main",
                TableName = tableName,
                IsUnique = isUnique,
                IsClustered = false,
                IsPrimaryKey = origin == "pk",
                ColumnNames = columnNames,
            });
        }
    }

    private static async Task CollectConstraintsAsync(DbConnection connection, string tableName, Dictionary<ColumnRef, List<ColumnConstraintDto>> columnConstraints, List<DbForeignKey> foreignKeys)
    {
        // Get primary key information from table_xinfo (pk column)
        var pkCmd = connection.CreateCommand();
        pkCmd.CommandText = $"PRAGMA table_xinfo({tableName})";

        await using var pkReader = await pkCmd.ExecuteReaderAsync();
        while (await pkReader.ReadAsync())
        {
            var hidden = (long)pkReader["hidden"];
            if (hidden == 1)
                continue;

            var isPk = ((long)pkReader["pk"]) > 0;
            if (!isPk)
                continue;

            var columnName = (string)pkReader["name"];
            var colRef = new ColumnRef("main", "main", tableName, columnName);

            if (!columnConstraints.ContainsKey(colRef))
                columnConstraints[colRef] = new List<ColumnConstraintDto>();

            columnConstraints[colRef].Add(new ColumnConstraintDto
            {
                ConstraintCatalog = "main",
                ConstraintSchema = "main",
                ConstraintName = $"PK_{tableName}",
                TableCatalog = "main",
                TableSchema = "main",
                TableName = tableName,
                ColumnName = columnName,
                ConstraintType = "PRIMARY KEY",
            });
        }

        // Get foreign key information. Each FK can span multiple columns;
        // foreign_key_list returns one row per child column with the same
        // `id`, so group by id to produce a single DbForeignKey per FK.
        var fkCmd = connection.CreateCommand();
        fkCmd.CommandText = $"PRAGMA foreign_key_list({tableName})";

        var fkRows = new List<(long id, string parentTable, string fromCol, string toCol)>();
        await using (var fkReader = await fkCmd.ExecuteReaderAsync())
        {
            while (await fkReader.ReadAsync())
            {
                var columnName = (string)fkReader["from"];
                var colRef = new ColumnRef("main", "main", tableName, columnName);

                if (!columnConstraints.ContainsKey(colRef))
                    columnConstraints[colRef] = new List<ColumnConstraintDto>();

                var fkId = (long)fkReader["id"];
                columnConstraints[colRef].Add(new ColumnConstraintDto
                {
                    ConstraintCatalog = "main",
                    ConstraintSchema = "main",
                    ConstraintName = $"FK_{tableName}_{fkId}",
                    TableCatalog = "main",
                    TableSchema = "main",
                    TableName = tableName,
                    ColumnName = columnName,
                    ConstraintType = "FOREIGN KEY",
                });

                fkRows.Add((fkId, (string)fkReader["table"], columnName, (string)fkReader["to"]));
            }
        }

        foreach (var group in fkRows.GroupBy(r => r.id))
        {
            var ordered = group.ToList();
            foreignKeys.Add(new DbForeignKey
            {
                ConstraintName = $"FK_{tableName}_{group.Key}",
                ChildTableSchema = "main",
                ChildTableName = tableName,
                ChildColumnNames = ordered.Select(r => r.fromCol).ToArray(),
                ParentTableSchema = "main",
                ParentTableName = ordered[0].parentTable,
                ParentColumnNames = ordered.Select(r => r.toCol).ToArray(),
            });
        }

        // Get unique index information
        var uniqueCmd = connection.CreateCommand();
        uniqueCmd.CommandText = $"PRAGMA index_list({tableName})";

        await using var uniqueReader = await uniqueCmd.ExecuteReaderAsync();
        var uniqueIndexes = new List<(string name, bool isUnique)>();
        while (await uniqueReader.ReadAsync())
        {
            var indexName = (string)uniqueReader["name"];
            var isUnique = (long)uniqueReader["unique"] == 1;
            uniqueIndexes.Add((indexName, isUnique));
        }

        foreach (var (indexName, isUnique) in uniqueIndexes)
        {
            if (!isUnique)
                continue;

            var indexInfoCmd = connection.CreateCommand();
            indexInfoCmd.CommandText = $"PRAGMA index_info({indexName})";

            await using var indexInfoReader = await indexInfoCmd.ExecuteReaderAsync();
            while (await indexInfoReader.ReadAsync())
            {
                var columnName = (string)indexInfoReader["name"];
                var colRef = new ColumnRef("main", "main", tableName, columnName);

                if (!columnConstraints.ContainsKey(colRef))
                    columnConstraints[colRef] = new List<ColumnConstraintDto>();

                // Skip if already has PRIMARY KEY constraint (SQLite often creates unique index for PK)
                if (columnConstraints[colRef].Any(c => c.ConstraintType == "PRIMARY KEY"))
                    continue;

                columnConstraints[colRef].Add(new ColumnConstraintDto
                {
                    ConstraintCatalog = "main",
                    ConstraintSchema = "main",
                    ConstraintName = indexName,
                    TableCatalog = "main",
                    TableSchema = "main",
                    TableName = tableName,
                    ColumnName = columnName,
                    ConstraintType = "UNIQUE",
                });
            }
        }
    }
}
