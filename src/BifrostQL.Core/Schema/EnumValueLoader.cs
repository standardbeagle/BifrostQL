using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Schema;

/// <summary>
/// Loads and sanitizes the DISTINCT values of each <c>enum:</c>-marked lookup
/// table at schema-build time, resolving each table's effective value column.
///
/// One table's failure (missing column, query error) degrades only that table to
/// a plain scalar — it never fails the whole load.
/// </summary>
public static class EnumValueLoader
{
    /// <summary>
    /// The result of loading every enum lookup table.
    /// </summary>
    /// <param name="Values">Map of table DB name → sanitized enum entries.</param>
    /// <param name="ValueColumns">Map of table DB name → resolved value column.</param>
    public sealed record LoadResult(
        IReadOnlyDictionary<string, IReadOnlyList<EnumValueEntry>> Values,
        IReadOnlyDictionary<string, string> ValueColumns);

    /// <summary>
    /// Resolves the effective value column for an enum table:
    /// the explicitly configured column when present, otherwise the first non-PK
    /// string column (type contains "char" or "text"). Returns null when the
    /// table is not enum-configured or no suitable column exists.
    /// </summary>
    public static string? ResolveValueColumn(IDbTable table)
    {
        var cfg = EnumTableConfig.FromTable(table);
        if (cfg == null)
            return null;

        if (!string.IsNullOrEmpty(cfg.ValueColumn))
            return cfg.ValueColumn;

        foreach (var column in table.Columns)
        {
            if (column.IsPrimaryKey)
                continue;

            var type = StringNormalizer.NormalizeType(column.EffectiveDataType);
            if (type.Contains("char") || type.Contains("text"))
                return column.ColumnName;
        }

        return null;
    }

    /// <summary>
    /// Loads the DISTINCT values of every enum lookup table over a single shared
    /// connection. Each table's load is isolated: a <see cref="DbException"/>
    /// degrades that table to an empty value set and the load continues.
    /// </summary>
    /// <param name="model">The database model whose enum tables are loaded.</param>
    /// <param name="connFactory">Connection + dialect provider.</param>
    /// <param name="whereByTable">Optional per-table WHERE SQL (security; wired later).</param>
    public static async Task<LoadResult> LoadAsync(
        IDbModel model,
        IDbConnFactory connFactory,
        IReadOnlyDictionary<string, string>? whereByTable = null)
    {
        var values = new Dictionary<string, IReadOnlyList<EnumValueEntry>>();
        var valueColumns = new Dictionary<string, string>();
        var dialect = connFactory.Dialect;

        await using var conn = connFactory.GetConnection();
        await conn.OpenAsync();

        foreach (var table in model.Tables)
        {
            var valueColumn = ResolveValueColumn(table);
            if (valueColumn == null)
                continue;

            valueColumns[table.DbName] = valueColumn;

            try
            {
                values[table.DbName] = await LoadTableAsync(
                    conn, dialect, table, valueColumn, whereByTable);
            }
            catch (DbException)
            {
                // Degrade this table to a plain scalar; never fail the whole load.
                values[table.DbName] = Array.Empty<EnumValueEntry>();
            }
        }

        return new LoadResult(values, valueColumns);
    }

    private static async Task<IReadOnlyList<EnumValueEntry>> LoadTableAsync(
        DbConnection conn,
        QueryModel.ISqlDialect dialect,
        IDbTable table,
        string valueColumn,
        IReadOnlyDictionary<string, string>? whereByTable)
    {
        var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
        var escapedColumn = dialect.EscapeIdentifier(valueColumn);
        var sql = $"SELECT DISTINCT {escapedColumn} FROM {tableRef}";

        if (whereByTable != null
            && whereByTable.TryGetValue(table.DbName, out var where)
            && !string.IsNullOrEmpty(where))
        {
            sql += $" WHERE {where}";
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var raw = new List<string?>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var value = reader.GetValue(0);
            raw.Add(value is DBNull ? null : value.ToString());
        }

        return EnumValueSanitizer.SanitizeAll(raw);
    }
}
