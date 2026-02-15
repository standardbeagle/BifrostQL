using System.Data;
using System.Data.Common;
using BifrostQL.Core.Model;

namespace BifrostQL.Sqlite;

/// <summary>
/// SQLite implementation of schema reader using pragma statements.
/// SQLite doesn't have information_schema, so we use PRAGMA commands.
/// </summary>
public sealed class SqliteSchemaReader : ISchemaReader
{
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
            var tableColumns = new List<ColumnDto>();
            var ordinal = 1;

            while (await colReader.ReadAsync())
            {
                var columnName = (string)colReader["name"];
                var dataType = (string)colReader["type"];
                var notNull = ((long)colReader["notnull"]) == 1;
                var defaultValue = colReader["dflt_value"] is DBNull ? null : colReader["dflt_value"]?.ToString();
                var isPk = ((long)colReader["pk"]) > 0;

                // SQLite AUTOINCREMENT is detected via INTEGER PRIMARY KEY
                var isIdentity = isPk && dataType.Equals("INTEGER", StringComparison.OrdinalIgnoreCase);

                var column = new ColumnDto(
                    TableCatalog: "main",
                    TableSchema: "main",
                    TableName: tableName,
                    ColumnName: columnName,
                    OrdinalPosition: ordinal++,
                    ColumnDefault: defaultValue,
                    IsNullable: notNull ? "NO" : "YES",
                    DataType: dataType,
                    CharacterMaximumLength: null,
                    CharacterOctetLength: null,
                    NumericPrecision: null,
                    NumericPrecisionRadix: null,
                    NumericScale: null,
                    DateTimePrecision: null,
                    CharacterSetCatalog: null,
                    CharacterSetSchema: null,
                    CharacterSetName: null,
                    CollationCatalog: null,
                    CollationSchema: null,
                    CollationName: null,
                    DomainCatalog: null,
                    DomainSchema: null,
                    DomainName: null,
                    IsIdentity: isIdentity
                );

                tableColumns.Add(column);
                allColumns.Add(column);

                // Add PRIMARY KEY constraint
                if (isPk)
                {
                    var colRef = new ColumnRef("main", "main", tableName, columnName);
                    if (!columnConstraints.ContainsKey(colRef))
                        columnConstraints[colRef] = new List<ColumnConstraintDto>();

                    columnConstraints[colRef].Add(new ColumnConstraintDto(
                        TableCatalog: "main",
                        TableSchema: "main",
                        TableName: tableName,
                        ColumnName: columnName,
                        ConstraintCatalog: "main",
                        ConstraintSchema: "main",
                        ConstraintName: $"PK_{tableName}",
                        ConstraintType: "PRIMARY KEY"
                    ));
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
                columnConstraints[colRef].Add(new ColumnConstraintDto(
                    TableCatalog: "main",
                    TableSchema: "main",
                    TableName: tableName,
                    ColumnName: columnName,
                    ConstraintCatalog: "main",
                    ConstraintSchema: "main",
                    ConstraintName: $"FK_{tableName}_{fkId}",
                    ConstraintType: "FOREIGN KEY"
                ));
            }

            var dbTable = new DbTable(
                TableCatalog: "main",
                TableSchema: "main",
                TableName: tableName,
                TableType: tableType == "view" ? "VIEW" : "BASE TABLE",
                Columns: tableColumns.ToArray()
            );

            tables.Add(dbTable);
        }

        return new SchemaData(columnConstraints, allColumns.ToArray(), tables);
    }
}
