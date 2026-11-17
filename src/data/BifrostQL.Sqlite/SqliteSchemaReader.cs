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

        // Every per-table read binds the name as a value through the pragma
        // table-valued functions; a name from sqlite_master is data, never SQL text.
        foreach (var (tableName, tableType) in tableNames)
        {
            var rawCols = await ReadColumnsAsync(connection, tableName);
            var tableIndexes = await ReadIndexesAsync(connection, tableName);
            CollectConstraints(tableName, rawCols, tableIndexes, columnConstraints);
            await CollectForeignKeysAsync(connection, tableName, columnConstraints, foreignKeys);
            CollectSortableIndexes(tableName, tableIndexes, indexes);

            // AUTOINCREMENT requires single INTEGER PRIMARY KEY - composite PKs cannot be identity
            var pkCount = rawCols.Count(c => c.IsPk);
            var tableColumns = new List<ColumnDto>();
            var ordinal = 1;

            foreach (var (columnName, dataType, notNull, isPk, isComputed, hasDefault) in rawCols)
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
                    HasDefault = hasDefault,
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

    private sealed record RawColumn(string Name, string Type, bool NotNull, bool IsPk, bool IsComputed, bool HasDefault);

    /// <summary>One index row from <c>pragma_index_list</c>, with its key columns (null for an expression key).</summary>
    private sealed record RawIndex(string Name, bool IsUnique, bool IsPartial, string Origin, IReadOnlyList<string?> Columns);

    private static DbCommand Command(DbConnection connection, string sql, string name)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = name;
        cmd.Parameters.Add(p);
        return cmd;
    }

    private static async Task<List<RawColumn>> ReadColumnsAsync(DbConnection connection, string tableName)
    {
        await using var cmd = Command(connection,
            "SELECT name, type, \"notnull\", pk, hidden, dflt_value FROM pragma_table_xinfo(@name)", tableName);
        await using var reader = await cmd.ExecuteReaderAsync();
        var columns = new List<RawColumn>();
        while (await reader.ReadAsync())
        {
            // hidden: 0=normal, 1=hidden rowid, 2=virtual generated, 3=stored generated
            var hidden = (long)reader["hidden"];
            if (hidden == 1)
                continue;
            columns.Add(new RawColumn(
                (string)reader["name"],
                (string)reader["type"],
                (long)reader["notnull"] == 1,
                (long)reader["pk"] > 0,
                hidden is 2 or 3,
                reader["dflt_value"] is not DBNull));
        }
        return columns;
    }

    private static async Task<List<RawIndex>> ReadIndexesAsync(DbConnection connection, string tableName)
    {
        var rows = new List<(string Name, bool IsUnique, bool IsPartial, string Origin)>();
        await using (var listCmd = Command(connection,
                         "SELECT name, \"unique\", origin, partial FROM pragma_index_list(@name)", tableName))
        await using (var listReader = await listCmd.ExecuteReaderAsync())
        {
            while (await listReader.ReadAsync())
            {
                rows.Add((
                    (string)listReader["name"],
                    (long)listReader["unique"] == 1,
                    (long)listReader["partial"] == 1,
                    (string)listReader["origin"]));
            }
        }

        var indexes = new List<RawIndex>(rows.Count);
        foreach (var row in rows)
        {
            var columns = new List<string?>();
            await using var infoCmd = Command(connection,
                "SELECT name FROM pragma_index_info(@name) ORDER BY seqno", row.Name);
            await using var infoReader = await infoCmd.ExecuteReaderAsync();
            while (await infoReader.ReadAsync())
                columns.Add(infoReader["name"] as string);
            indexes.Add(new RawIndex(row.Name, row.IsUnique, row.IsPartial, row.Origin, columns));
        }
        return indexes;
    }

    /// <summary>
    /// Collects every index on <paramref name="tableName"/> (unique and not) a client
    /// could sort by. Partial indexes are skipped — they cover a subset of rows, so
    /// they cannot serve an arbitrary ORDER BY. Expression-key indexes are skipped for
    /// the same reason: index_info reports a NULL column name for expression keys, and
    /// there is no column a client could sort by. SQLite rowid tables are physically
    /// ordered by rowid, so no index is marked clustered.
    /// </summary>
    private static void CollectSortableIndexes(string tableName, IEnumerable<RawIndex> tableIndexes, List<DbIndex> indexes)
    {
        foreach (var index in tableIndexes)
        {
            if (index.IsPartial || index.Columns.Count == 0 || index.Columns.Any(c => c is null))
                continue;
            indexes.Add(new DbIndex
            {
                Name = index.Name,
                TableSchema = "main",
                TableName = tableName,
                IsUnique = index.IsUnique,
                IsClustered = false,
                IsPrimaryKey = index.Origin == "pk",
                ColumnNames = index.Columns.Select(c => c!).ToList(),
            });
        }
    }

    private static List<ColumnConstraintDto> ConstraintsFor(Dictionary<ColumnRef, List<ColumnConstraintDto>> columnConstraints, ColumnRef colRef)
    {
        if (!columnConstraints.TryGetValue(colRef, out var list))
        {
            list = new List<ColumnConstraintDto>();
            columnConstraints[colRef] = list;
        }
        return list;
    }

    private static ColumnConstraintDto Constraint(string tableName, string columnName, string name, string type) => new()
    {
        ConstraintCatalog = "main",
        ConstraintSchema = "main",
        ConstraintName = name,
        TableCatalog = "main",
        TableSchema = "main",
        TableName = tableName,
        ColumnName = columnName,
        ConstraintType = type,
    };

    private static void CollectConstraints(string tableName, IEnumerable<RawColumn> columns, IEnumerable<RawIndex> tableIndexes,
        Dictionary<ColumnRef, List<ColumnConstraintDto>> columnConstraints)
    {
        foreach (var column in columns.Where(c => c.IsPk))
        {
            var colRef = new ColumnRef("main", "main", tableName, column.Name);
            ConstraintsFor(columnConstraints, colRef).Add(Constraint(tableName, column.Name, $"PK_{tableName}", "PRIMARY KEY"));
        }

        foreach (var index in tableIndexes.Where(i => i.IsUnique))
        {
            foreach (var columnName in index.Columns)
            {
                // An expression key has no column to mark.
                if (columnName is null)
                    continue;
                var colRef = new ColumnRef("main", "main", tableName, columnName);
                var constraints = ConstraintsFor(columnConstraints, colRef);
                // Skip if already has PRIMARY KEY constraint (SQLite often creates unique index for PK)
                if (constraints.Any(c => c.ConstraintType == "PRIMARY KEY"))
                    continue;
                constraints.Add(Constraint(tableName, columnName, index.Name, "UNIQUE"));
            }
        }
    }

    /// <summary>
    /// Each FK can span multiple columns; foreign_key_list returns one row per child
    /// column with the same <c>id</c>, so group by id to produce a single
    /// <see cref="DbForeignKey"/> per FK.
    /// </summary>
    private static async Task CollectForeignKeysAsync(DbConnection connection, string tableName,
        Dictionary<ColumnRef, List<ColumnConstraintDto>> columnConstraints, List<DbForeignKey> foreignKeys)
    {
        var fkRows = new List<(long id, string parentTable, string fromCol, string toCol)>();
        await using (var fkCmd = Command(connection,
                         "SELECT id, seq, \"table\", \"from\", \"to\" FROM pragma_foreign_key_list(@name) ORDER BY id, seq", tableName))
        await using (var fkReader = await fkCmd.ExecuteReaderAsync())
        {
            while (await fkReader.ReadAsync())
            {
                var columnName = (string)fkReader["from"];
                var fkId = (long)fkReader["id"];
                var colRef = new ColumnRef("main", "main", tableName, columnName);
                ConstraintsFor(columnConstraints, colRef).Add(Constraint(tableName, columnName, $"FK_{tableName}_{fkId}", "FOREIGN KEY"));
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
    }
}
