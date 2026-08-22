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

    /// <summary>
    /// Minimal forward-only reader feeding <see cref="SqlBulkCopy"/> one staged row at a
    /// time: the four control columns first, then each staged data column under its
    /// <c>__c_</c> name. Values pass through as-is; SqlBulkCopy converts them against the
    /// staging table's cloned target types.
    /// </summary>
    private sealed class StagedRowDataReader : DbDataReader
    {
        private readonly IReadOnlyList<string> _columns;
        private readonly IReadOnlyList<BulkStagedAction> _rows;
        private int _index = -1;

        public StagedRowDataReader(IReadOnlyList<string> columns, IReadOnlyList<BulkStagedAction> rows)
        {
            _columns = columns;
            _rows = rows;
        }

        private BulkStagedAction Current => _rows[_index];

        public override int FieldCount => 4 + _columns.Count;

        public override bool Read() => ++_index < _rows.Count;

        public override string GetName(int ordinal) => ordinal switch
        {
            0 => SqlServerBulkBatchSql.SeqColumn,
            1 => SqlServerBulkBatchSql.OpColumn,
            2 => SqlServerBulkBatchSql.GroupColumn,
            3 => SqlServerBulkBatchSql.ConflictColumn,
            _ => SqlServerBulkBatchSql.StagedColumn(_columns[ordinal - 4]),
        };

        public override int GetOrdinal(string name)
        {
            for (var i = 0; i < FieldCount; i++)
                if (string.Equals(GetName(i), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            throw new IndexOutOfRangeException(name);
        }

        public override object GetValue(int ordinal)
        {
            var value = ordinal switch
            {
                0 => Current.Seq,
                1 => SqlServerBulkBatchSql.OpLetter(Current.Op).ToString(),
                2 => (byte)Current.Group,
                3 => Current.ConflictOnNoRows,
                _ => Current.Values.TryGetValue(_columns[ordinal - 4], out var v) ? v : null,
            };
            return value ?? DBNull.Value;
        }

        public override bool IsDBNull(int ordinal) => GetValue(ordinal) == DBNull.Value;

        // SqlBulkCopy drives the reader through Read/FieldCount/GetValue/GetName only;
        // the remaining DbDataReader surface is deliberately unsupported.
        public override bool NextResult() => false;
        public override int Depth => 0;
        public override bool HasRows => _rows.Count > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => (char)GetValue(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();
        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
        public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
        public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
        public override Type GetFieldType(int ordinal) => GetValue(ordinal) is var v && v != DBNull.Value ? v.GetType() : typeof(object);
        public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
        public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
        public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
        public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
        public override string GetString(int ordinal) => (string)GetValue(ordinal);
        public override int GetValues(object[] values)
        {
            var count = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < count; i++)
                values[i] = GetValue(i);
            return count;
        }
    }
}
