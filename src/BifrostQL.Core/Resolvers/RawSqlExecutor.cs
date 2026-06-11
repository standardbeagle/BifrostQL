using System.Data.Common;
using BifrostQL.Core.Model;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// A single column in a <see cref="RawSqlResult"/>.
    /// <paramref name="Type"/> is the provider's data-type name as reported by
    /// <see cref="DbDataReader.GetDataTypeName"/> (e.g. "int", "TEXT", "varchar").
    /// </summary>
    public sealed record RawSqlColumn(string Name, string Type);

    /// <summary>
    /// Columnar result of a raw SQL execution. Rows are positional arrays aligned
    /// to <see cref="Columns"/> rather than name-keyed dictionaries so that column
    /// order is preserved and duplicate / empty column names don't collide — both
    /// of which matter for grid rendering. For non-result statements
    /// (INSERT/UPDATE/DELETE/DDL) <see cref="Columns"/> and <see cref="Rows"/> are
    /// empty and <see cref="RowsAffected"/> carries the count.
    /// </summary>
    public sealed record RawSqlResult(
        IReadOnlyList<RawSqlColumn> Columns,
        IReadOnlyList<object?[]> Rows,
        int RowsAffected,
        bool Truncated);

    /// <summary>
    /// Executes raw SQL against an <see cref="IDbConnFactory"/> and returns a
    /// columnar <see cref="RawSqlResult"/>. Shared by the GraphQL
    /// <see cref="RawSqlQueryResolver"/> (which adapts the columnar shape back to
    /// name-keyed rows) and the Photino in-process SQL bridge (which forwards the
    /// columnar shape straight to the grid).
    ///
    /// This executor performs no SQL validation — callers that need to restrict
    /// statement kinds (e.g. SELECT-only) validate before calling. The desktop
    /// bridge intentionally allows full DML/DDL.
    /// </summary>
    public static class RawSqlExecutor
    {
        public static async Task<RawSqlResult> ExecuteAsync(
            IDbConnFactory connFactory,
            string sql,
            IReadOnlyDictionary<string, object?>? parameters,
            int timeoutSeconds,
            int maxRows,
            CancellationToken ct = default,
            ILogger? logger = null)
        {
            await using var conn = connFactory.GetConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            if (parameters != null)
            {
                foreach (var (name, value) in parameters)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = name.StartsWith("@") ? name : $"@{name}";
                    p.Value = value ?? DBNull.Value;
                    cmd.Parameters.Add(p);
                }
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            // Non-result statement (INSERT/UPDATE/DELETE/DDL): no columns to read,
            // just report the affected-row count.
            if (reader.FieldCount == 0)
                return new RawSqlResult(Array.Empty<RawSqlColumn>(), Array.Empty<object?[]>(), reader.RecordsAffected, false);

            var columns = new RawSqlColumn[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var columnName = reader.GetName(i);
                columns[i] = new RawSqlColumn(columnName, SafeDataTypeName(reader, i, columnName, logger));
            }

            var rows = new List<object?[]>();
            var truncated = false;
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }

                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var val = reader.GetValue(i);
                    row[i] = val == DBNull.Value ? null : val;
                }
                rows.Add(row);
            }

            return new RawSqlResult(columns, rows, reader.RecordsAffected, truncated);
        }

        // Some providers throw from GetDataTypeName for computed/expression columns.
        // The type label is advisory grid metadata, so degrade to "" rather than failing the query.
        private static string SafeDataTypeName(DbDataReader reader, int ordinal, string? columnName = null, ILogger? logger = null)
        {
            try { return reader.GetDataTypeName(ordinal); }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "GetDataTypeName failed for column {ColumnIndex} ({ColumnName}); type will be reported as empty string", ordinal, columnName ?? $"<ordinal {ordinal}>");
                return string.Empty;
            }
        }
    }
}
