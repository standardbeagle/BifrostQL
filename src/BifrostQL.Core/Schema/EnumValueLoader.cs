using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Schema;

/// <summary>
/// Loads and sanitizes the DISTINCT values of each <c>enum:</c>-marked lookup
/// table at schema-build time, resolving each table's effective value column.
///
/// One table's failure (missing column, query error) degrades only that table to
/// a plain scalar — it never fails the whole load — and is logged through
/// <see cref="BifrostErrorSink.Logger"/> so the degradation is visible.
/// </summary>
public static class EnumValueLoader
{
    /// <summary>
    /// The result of loading every enum lookup table.
    /// </summary>
    /// <param name="Values">Map of schema-qualified table → sanitized enum entries.</param>
    /// <param name="ValueColumns">Map of schema-qualified table → resolved value column.</param>
    public sealed record LoadResult(
        IReadOnlyDictionary<EnumTableKey, IReadOnlyList<EnumValueEntry>> Values,
        IReadOnlyDictionary<EnumTableKey, string> ValueColumns);

    /// <summary>
    /// Loads the DISTINCT values of every enum lookup table over a single shared
    /// connection. Each table's load is isolated: a <see cref="DbException"/>
    /// degrades that table to an empty value set and the load continues.
    /// </summary>
    /// <param name="model">The database model whose enum tables are loaded.</param>
    /// <param name="connFactory">Connection + dialect provider.</param>
    /// <param name="cancellationToken">Cancels the open and every per-table read.</param>
    public static async Task<LoadResult> LoadAsync(
        IDbModel model,
        IDbConnFactory connFactory,
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<EnumTableKey, IReadOnlyList<EnumValueEntry>>();
        var valueColumns = new Dictionary<EnumTableKey, string>();
        var dialect = connFactory.Dialect;

        await using var conn = connFactory.GetConnection();
        await conn.OpenAsync(cancellationToken);

        foreach (var table in model.Tables)
        {
            var cfg = EnumTableConfig.FromTable(table);
            if (cfg == null)
                continue;

            var valueColumn = cfg.ResolveValueColumn(table);
            if (valueColumn == null)
                continue;

            var key = EnumTableKey.Of(table);
            valueColumns[key] = valueColumn;

            try
            {
                values[key] = await LoadTableAsync(
                    conn, dialect, table, valueColumn, cancellationToken);
            }
            // Some providers surface cancellation as a DbException; a cancelled load
            // must propagate, not degrade every remaining table.
            catch (DbException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Degrade this table to a plain scalar; never fail the whole load.
                BifrostErrorSink.Logger?.LogWarning(ex,
                    "Enum lookup table {Table} failed to load its values from column {Column}; " +
                    "its referencing columns degrade to plain scalars.",
                    key.ToString(), valueColumn);
                values[key] = Array.Empty<EnumValueEntry>();
            }
        }

        return new LoadResult(values, valueColumns);
    }

    private static async Task<IReadOnlyList<EnumValueEntry>> LoadTableAsync(
        DbConnection conn,
        QueryModel.ISqlDialect dialect,
        IDbTable table,
        string valueColumn,
        CancellationToken cancellationToken)
    {
        var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
        var escapedColumn = dialect.EscapeIdentifier(valueColumn);
        var sql = $"SELECT DISTINCT {escapedColumn} FROM {tableRef}";

        // Soft-delete is a context-free predicate, so it is intrinsic to enum
        // membership: soft-deleted lookup rows must not become enum members.
        var softDeleteColumn = table.GetMetadataValue(MetadataKeys.SoftDelete.Column);
        if (!string.IsNullOrWhiteSpace(softDeleteColumn)
            && table.ColumnLookup.ContainsKey(softDeleteColumn))
        {
            var escapedSoftDelete = dialect.EscapeIdentifier(softDeleteColumn);
            sql += $" WHERE {escapedSoftDelete} IS NULL";
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var raw = new List<string?>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = reader.GetValue(0);
            raw.Add(value is DBNull ? null : value.ToString());
        }

        return EnumValueSanitizer.SanitizeAll(raw);
    }
}
