using System.Data;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers.BulkBatch;
using Npgsql;
using Npgsql.Schema;
using NpgsqlTypes;

namespace BifrostQL.Ngsql;

/// <summary>
/// Streams staged rows into the temp staging table with PostgreSQL binary COPY
/// (<see cref="NpgsqlConnection.BeginBinaryImportAsync"/>). Binary COPY is strict about wire
/// types, so each column's <see cref="NpgsqlDbType"/> is read back from the staging table
/// itself (a schema-only SELECT) and every value is written AS that type — Npgsql's handlers
/// then convert compatible CLR values. Any failure aborts the COPY (nothing persists),
/// clears the staging table, and reports false so the caller falls back to the chunked
/// parameterized load — streaming is a performance strategy, never a semantics change.
/// </summary>
internal static class PostgresStagingStream
{
    public static async Task<bool> TryLoadAsync(
        NpgsqlConnection connection, ISqlDialect dialect, string stagingName, BulkBatchPlan plan, CancellationToken ct)
    {
        var stage = dialect.EscapeIdentifier(stagingName);
        var columnNames = new List<string>
        {
            StagedBulkBatchExecutorBase.SeqColumn,
            StagedBulkBatchExecutorBase.OpColumn,
            StagedBulkBatchExecutorBase.GroupColumn,
            StagedBulkBatchExecutorBase.ConflictColumn,
        };
        columnNames.AddRange(plan.StagingColumns.Select(StagedBulkBatchExecutorBase.StagedColumn));
        var escapedColumns = columnNames.Select(dialect.EscapeIdentifier).ToList();

        try
        {
            var columnTypes = await ReadColumnTypesAsync(connection, stage, escapedColumns, ct);
            if (columnTypes is null)
                return false; // a column's NpgsqlDbType is unknown — let the parameterized load coerce

            await using var importer = await connection.BeginBinaryImportAsync(
                $"COPY {stage} ({string.Join(",", escapedColumns)}) FROM STDIN (FORMAT BINARY)", ct);
            foreach (var row in plan.Rows)
            {
                await importer.StartRowAsync(ct);
                await WriteAsync(importer, row.Seq, columnTypes[0], ct);
                await WriteAsync(importer, StagedBulkBatchExecutorBase.OpLetter(row.Op).ToString(), columnTypes[1], ct);
                await WriteAsync(importer, (short)row.Group, columnTypes[2], ct);
                await WriteAsync(importer, row.ConflictOnNoRows, columnTypes[3], ct);
                for (var c = 0; c < plan.StagingColumns.Count; c++)
                {
                    var value = row.Values.TryGetValue(plan.StagingColumns[c], out var v) ? v : null;
                    await WriteAsync(importer, value, columnTypes[4 + c], ct);
                }
            }
            await importer.CompleteAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An aborted COPY persists nothing, but clear defensively so the chunked
            // fallback never runs on top of partial rows.
            await using var truncate = connection.CreateCommand();
            truncate.CommandText = $"TRUNCATE {stage};";
            await truncate.ExecuteNonQueryAsync(CancellationToken.None);
            return false;
        }
    }

    private static async Task WriteAsync(NpgsqlBinaryImporter importer, object? value, NpgsqlDbType type, CancellationToken ct)
    {
        if (value is null or DBNull)
            await importer.WriteNullAsync(ct);
        else
            await importer.WriteAsync(value, type, ct);
    }

    private static async Task<IReadOnlyList<NpgsqlDbType>?> ReadColumnTypesAsync(
        NpgsqlConnection connection, string stage, IReadOnlyList<string> escapedColumns, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(",", escapedColumns)} FROM {stage} LIMIT 0;";
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct);
        var schema = reader.GetColumnSchema();
        var types = new NpgsqlDbType[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            var npgsqlType = (schema[i] as NpgsqlDbColumn)?.NpgsqlDbType;
            if (npgsqlType is null)
                return null;
            types[i] = npgsqlType.Value;
        }
        return types;
    }
}
