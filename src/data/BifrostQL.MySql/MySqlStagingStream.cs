using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers.BulkBatch;
using MySqlConnector;

namespace BifrostQL.MySql;

/// <summary>
/// Streams staged rows into the temp staging table with <see cref="MySqlBulkCopy"/> (built
/// on <c>LOAD DATA LOCAL INFILE</c>). The capability is opt-in on BOTH sides — the
/// connection string must set <c>AllowLoadLocalInfile=true</c> and the server must enable
/// <c>local_infile</c> — so absence of either simply reports false and the caller uses the
/// chunked parameterized load. LOAD DATA coerces bad values into WARNINGS rather than
/// errors, so a load that inserted a different row count or produced any warning is treated
/// as a FAILURE: the staging table is cleared and the chunked load (whose parameter
/// semantics match the per-row path exactly) takes over — streaming is a performance
/// strategy, never a semantics change.
/// </summary>
internal static class MySqlStagingStream
{
    public static async Task<bool> TryLoadAsync(
        MySqlConnection connection, ISqlDialect dialect, string stagingName, BulkBatchPlan plan, CancellationToken ct)
    {
        if (!new MySqlConnectionStringBuilder(connection.ConnectionString).AllowLoadLocalInfile)
            return false;

        var stage = dialect.EscapeIdentifier(stagingName);
        try
        {
            var bulkCopy = new MySqlBulkCopy(connection) { DestinationTableName = stage };
            // The staged flag column is cloned as an integer (CAST(NULL AS SIGNED)), and
            // LOAD DATA's text serialization will not coerce a CLR bool — emit 1/0.
            using var reader = new StagedRowDataReader(plan.StagingColumns, plan.Rows, conflictAsInt: true);
            for (var i = 0; i < reader.FieldCount; i++)
                bulkCopy.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, reader.GetName(i)));

            var result = await bulkCopy.WriteToServerAsync(reader, ct);
            if (result.RowsInserted == plan.Rows.Count && result.Warnings.Count == 0)
                return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // fall through to the cleanup + chunked fallback below
        }

        // Partial or warned load: clear the staging table so the chunked fallback never
        // runs on top of coerced rows.
        await using var truncate = connection.CreateCommand();
        truncate.CommandText = $"TRUNCATE {stage};";
        await truncate.ExecuteNonQueryAsync(CancellationToken.None);
        return false;
    }
}
