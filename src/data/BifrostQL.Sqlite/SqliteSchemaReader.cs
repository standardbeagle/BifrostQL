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

        // Get all tables
        var tablesCmd = connection.CreateCommand();
        tablesCmd.CommandText = "SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name";

        await using var tablesReader = await tablesCmd.ExecuteReaderAsync();
        var tableNames = new List<(string name, string type)>();
        while (await tablesReader.ReadAsync())
        {
            tableNames.Add(((string)tablesReader["name"], (string)tablesReader["type"]));
        }

        // For each table, get column information
        foreach (var (tableName, tableType) in tableNames)
        {
            var columnsCmd = connection.CreateCommand();
            columnsCmd.CommandText = $"PRAGMA table_info({tableName})";

            await using var colReader = await columnsCmd.ExecuteReaderAsync();
            var rawCols = new List<(string name, string type, bool notNull, bool isPk)>();

            while (await colReader.ReadAsync())
            {
                rawCols.Add((
                    (string)colReader["name"],
                    (string)colReader["type"],
                    ((long)colReader["notnull"]) == 1,
                    ((long)colReader["pk"]) > 0
                ));
            }

            // AUTOINCREMENT requires single INTEGER PRIMARY KEY - composite PKs cannot be identity
            var pkCount = rawCols.Count(c => c.isPk);
            var tableColumns = new List<ColumnDto>();
            var ordinal = 1;

            foreach (var (columnName, dataType, notNull, isPk) in rawCols)
            {
                var isIdentity = pkCount == 1 && isPk && dataType.Equals("INTEGER", StringComparison.OrdinalIgnoreCase);
                var columnRef = new ColumnRef("main", "main", tableName, columnName);

                var column = new ColumnDto
                {
                    TableCatalog = "main",
                    TableSchema = "main",
                    TableName = tableName,
                    ColumnName = columnName,
                    GraphQlName = columnName.ToGraphQl("col"),
                    NormalizedName = NormalizeColumn(columnName),
                    ColumnRef = columnRef,
                    DataType = dataType,
                    IsNullable = isPk ? false : !notNull,
                    OrdinalPosition = ordinal++,
                    IsIdentity = isIdentity,
                    IsPrimaryKey = isPk,
                };

                tableColumns.Add(column);
                allColumns.Add(column);

                if (isPk)
                {
                    if (!columnConstraints.ContainsKey(columnRef))
                        columnConstraints[columnRef] = new List<ColumnConstraintDto>();

                    columnConstraints[columnRef].Add(new ColumnConstraintDto
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
            }

            // Get foreign key information
            var fkCmd = connection.CreateCommand();
            fkCmd.CommandText = $"PRAGMA foreign_key_list({tableName})";

            await using var fkReader = await fkCmd.ExecuteReaderAsync();
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

        return new SchemaData(columnConstraints, allColumns.ToArray(), tables);
    }

    private static string NormalizeColumn(string column)
    {
        if (string.Equals("id", column, StringComparison.InvariantCultureIgnoreCase))
            return "id";
        if (column.EndsWith("id", StringComparison.InvariantCultureIgnoreCase))
        {
            var tableName = column.Substring(0, column.Length - 2);
            return new Pluralize.NET.Core.Pluralizer().Singularize(tableName);
        }
        return column;
    }
}
