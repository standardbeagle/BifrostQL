using System.Collections;
using System.Data;
using System.Data.Common;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Resolvers.BulkBatch;
using Microsoft.Data.SqlClient;

namespace BifrostQL.SqlServer;

/// <summary>
/// SQL Server's set-based batch capability: clone a clustered-indexed <c>#temp</c> staging
/// table from the target, <see cref="SqlBulkCopy"/> the staged rows into it, then apply every
/// op-group statement inside ONE inline SQL transaction (BEGIN TRANSACTION … COMMIT with
/// TRY/CATCH ROLLBACK). Staging runs OUTSIDE the transaction on purpose: a <c>#temp</c> dies
/// with the connection, so a staging failure has written nothing and surfaces as
/// <see cref="BulkBatchStagingException"/> — the pipeline's signal that the per-row path may
/// safely run the batch instead. Once the DML batch starts, failures are the batch's error.
/// </summary>
public sealed class SqlServerBulkBatchExecutor : IBulkBatchExecutor
{
    public static SqlServerBulkBatchExecutor Instance { get; } = new();

    public async Task<BulkBatchResult> ExecuteAsync(BulkBatchPlan plan, DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection is not SqlConnection sqlConnection)
            throw new InvalidOperationException(
                $"SqlServerBulkBatchExecutor requires a SqlConnection, but got '{connection.GetType().Name}'.");

        var dialect = SqlServerDialect.Instance;
        var suffix = Guid.NewGuid().ToString("N");
        var stagingName = $"#bifrost_batch_{suffix}";
        var outName = $"#bifrost_out_{suffix}";
        var tableRef = dialect.TableReference(plan.TableSchema, plan.TableDbName);

        try
        {
            await using (var ddl = sqlConnection.CreateCommand())
            {
                ddl.CommandText = SqlServerBulkBatchSql.BuildStagingDdl(dialect, tableRef, stagingName, outName, plan.StagingColumns);
                await ddl.ExecuteNonQueryAsync(cancellationToken);
            }

            using var bulkCopy = new SqlBulkCopy(sqlConnection) { DestinationTableName = dialect.EscapeIdentifier(stagingName) };
            using var reader = new StagedRowDataReader(plan.StagingColumns, plan.Rows);
            for (var i = 0; i < reader.FieldCount; i++)
                bulkCopy.ColumnMappings.Add(reader.GetName(i), reader.GetName(i));
            await bulkCopy.WriteToServerAsync(reader, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing has touched the target table yet; the #temp dies with the connection.
            throw new BulkBatchStagingException($"Bulk batch staging failed for '{tableRef}'.", ex);
        }

        int totalAffected;
        await using (var dml = sqlConnection.CreateCommand())
        {
            dml.CommandText = SqlServerBulkBatchSql.BuildDmlBatch(dialect, tableRef, stagingName, outName, plan.Groups);
            foreach (var parameter in DistinctFilterParameters(plan.Groups))
            {
                var p = dml.CreateParameter();
                p.ParameterName = parameter.Name;
                p.Value = parameter.Value ?? DBNull.Value;
                dml.Parameters.Add(p);
            }
            try
            {
                totalAffected = Convert.ToInt32(await dml.ExecuteScalarAsync(cancellationToken));
            }
            catch (SqlException ex) when (ex.Number == int.Parse(SqlServerBulkBatchSql.ConflictErrorNumber))
            {
                // Same condition, same signal as the per-row path: a concurrency-guarded row
                // matched nothing, so the whole batch rolled back.
                throw new BifrostExecutionError(
                    $"Update of '{plan.TableSchema}.{plan.TableDbName}' was rejected: the concurrency token no longer matches — the row was modified or removed since it was read. Reload and retry.")
                { ErrorCode = "CONFLICT" };
            }
        }

        var affectedSeqs = new List<int>();
        await using (var readBack = sqlConnection.CreateCommand())
        {
            readBack.CommandText = SqlServerBulkBatchSql.BuildAffectedSeqSelect(dialect, outName);
            await using var seqReader = await readBack.ExecuteReaderAsync(cancellationToken);
            while (await seqReader.ReadAsync(cancellationToken))
                affectedSeqs.Add(seqReader.GetInt32(0));
        }

        return new BulkBatchResult(totalAffected, affectedSeqs);
    }

    private static IEnumerable<SqlParameterInfo> DistinctFilterParameters(IReadOnlyList<BulkOpGroup> groups)
    {
        // The plan builder guarantees one canonical filter per batch, so parameters sharing
        // a name across groups are identical; bind each name once.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
            foreach (var parameter in group.FilterParameters)
                if (seen.Add(parameter.Name))
                    yield return parameter;
    }

}
