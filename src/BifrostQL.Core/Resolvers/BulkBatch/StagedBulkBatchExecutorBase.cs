using System.Data.Common;
using System.Globalization;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Resolvers.BulkBatch
{
    /// <summary>One statement of an op-group's application, in execution order.</summary>
    /// <param name="Sql">The statement text.</param>
    /// <param name="CountsTowardTotal">True for the group's data write (its DB-reported
    /// affected count sums into <see cref="BulkBatchResult.TotalAffected"/>); false for a
    /// helper statement such as MySQL's matched-row probe.</param>
    /// <param name="BindFilter">Whether the group's transformer-filter parameters bind on
    /// this statement's command.</param>
    public readonly record struct StagedStatement(string Sql, bool CountsTowardTotal, bool BindFilter);

    /// <summary>
    /// Provider-agnostic flow for dialects that stage batch rows through a session-scoped
    /// temp table and apply them with set-based DML inside a SQL-level transaction
    /// (<see cref="ISqlDialect.BeginTransactionSql"/> … COMMIT — never the ADO
    /// <see cref="DbTransaction"/> API). Subclasses supply only SQL text; everything here is
    /// plain <see cref="DbConnection"/>/<see cref="DbCommand"/> work:
    ///
    /// 1. STAGING (outside the transaction — a temp table dies with the connection, so a
    ///    failure here has written nothing and throws <see cref="BulkBatchStagingException"/>,
    ///    the pipeline's safe-fallback signal): staging DDL, then chunked multi-row
    ///    parameterized INSERTs — values bind exactly as the per-row path binds them, so the
    ///    engine's parameter coercion rules stay identical.
    /// 2. APPLY: BEGIN; per-group statements (each statement its own command on the open
    ///    session transaction, its DB-reported affected count summed when the statement is
    ///    the group's data write); then the conflict probe — any __conflict row absent from
    ///    the out-table means a concurrency-guarded write matched nothing, so ROLLBACK and
    ///    raise the pipeline's CONFLICT error; otherwise COMMIT.
    /// 3. Read the out-table's per-affected-row seq entries back on the same connection.
    ///
    /// SQL Server does not use this base: it ships its own single-command batch with an
    /// in-transaction THROW and SqlBulkCopy staging (<c>SqlServerBulkBatchExecutor</c>).
    /// </summary>
    public abstract class StagedBulkBatchExecutorBase : IBulkBatchExecutor
    {
        public const string SeqColumn = "__seq";
        public const string OpColumn = "__op";
        public const string GroupColumn = "__grp";
        public const string ConflictColumn = "__conflict";
        public const string StagedColumnPrefix = "__c_";

        /// <summary>Cap on bound parameters per staging INSERT chunk (well under every engine's limit).</summary>
        private const int MaxParametersPerChunk = 500;

        public static string StagedColumn(string column) => StagedColumnPrefix + column;

        public static char OpLetter(BulkOpCode op) => op switch
        {
            BulkOpCode.Insert => 'I',
            BulkOpCode.Update => 'U',
            BulkOpCode.Delete => 'D',
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };

        protected abstract ISqlDialect Dialect { get; }

        /// <summary>Staging DDL statements, executed one command each: the staging clone (typed
        /// from the target with nullability dropped), its seq index, and the out-table.</summary>
        protected abstract IReadOnlyList<string> BuildStagingDdl(
            string tableRef, string stagingName, string outName, IReadOnlyList<string> columns);

        /// <summary>The set-based statements applying one op-group, in order.</summary>
        protected abstract IReadOnlyList<StagedStatement> BuildGroupStatements(
            string tableRef, string stagingName, string outName, BulkOpGroup group);

        /// <summary>A scalar query returning a truthy value when any __conflict staged row has
        /// no out-table entry (its guarded write matched nothing).</summary>
        protected abstract string BuildConflictCheckSql(string stagingName, string outName);

        public async Task<BulkBatchResult> ExecuteAsync(BulkBatchPlan plan, DbConnection connection, CancellationToken cancellationToken)
        {
            var dialect = Dialect;
            var suffix = Guid.NewGuid().ToString("N");
            var stagingName = $"bifrost_batch_{suffix}";
            var outName = $"bifrost_out_{suffix}";
            var tableRef = dialect.TableReference(plan.TableSchema, plan.TableDbName);

            try
            {
                foreach (var ddl in BuildStagingDdl(tableRef, stagingName, outName, plan.StagingColumns))
                    await ExecuteTextAsync(connection, ddl, cancellationToken);
                await LoadStagingAsync(connection, dialect, stagingName, plan, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing has touched the target table; the temp tables die with the connection.
                throw new BulkBatchStagingException($"Bulk batch staging failed for '{tableRef}'.", ex);
            }

            var totalAffected = 0;
            await ExecuteTextAsync(connection, dialect.BeginTransactionSql, cancellationToken);
            try
            {
                foreach (var group in plan.Groups)
                {
                    foreach (var statement in BuildGroupStatements(tableRef, stagingName, outName, group))
                    {
                        await using var cmd = connection.CreateCommand();
                        cmd.CommandText = statement.Sql;
                        if (statement.BindFilter)
                            BindParameters(cmd, group.FilterParameters);
                        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                        if (statement.CountsTowardTotal)
                            totalAffected += affected;
                    }
                }

                await using (var conflictCmd = connection.CreateCommand())
                {
                    conflictCmd.CommandText = BuildConflictCheckSql(stagingName, outName);
                    var conflicted = await conflictCmd.ExecuteScalarAsync(cancellationToken);
                    if (IsTruthy(conflicted))
                    {
                        await ExecuteTextAsync(connection, dialect.RollbackTransactionSql, CancellationToken.None);
                        // Same condition, same signal as the per-row path.
                        throw new BifrostExecutionError(
                            $"Update of '{plan.TableSchema}.{plan.TableDbName}' was rejected: the concurrency token no longer matches — the row was modified or removed since it was read. Reload and retry.")
                        { ErrorCode = "CONFLICT" };
                    }
                }

                await ExecuteTextAsync(connection, dialect.CommitTransactionSql, cancellationToken);
            }
            catch (BifrostExecutionError)
            {
                throw;
            }
            catch
            {
                // Rollback must run even on cancellation so the session is left clean.
                try { await ExecuteTextAsync(connection, dialect.RollbackTransactionSql, CancellationToken.None); }
                catch { /* the connection is being torn down; the server rolls back with it */ }
                throw;
            }

            var affectedSeqs = new List<int>();
            await using (var readBack = connection.CreateCommand())
            {
                readBack.CommandText = $"SELECT {dialect.EscapeIdentifier(SeqColumn)} FROM {dialect.EscapeIdentifier(outName)};";
                await using var reader = await readBack.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    affectedSeqs.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
            }

            return new BulkBatchResult(totalAffected, affectedSeqs);
        }

        /// <summary>
        /// Loads the staged rows in chunked multi-row INSERTs. Every value binds as a named
        /// parameter (<c>@r&lt;row&gt;_&lt;col&gt;</c>) so the engine coerces CLR values under
        /// the same rules as the per-row path; chunk size keeps each command's parameter count
        /// under <see cref="MaxParametersPerChunk"/>.
        /// </summary>
        private static async Task LoadStagingAsync(
            DbConnection connection, ISqlDialect dialect, string stagingName, BulkBatchPlan plan, CancellationToken ct)
        {
            var columns = plan.StagingColumns;
            var columnList = string.Join(",",
                new[] { SeqColumn, OpColumn, GroupColumn, ConflictColumn }
                    .Concat(columns.Select(StagedColumn))
                    .Select(dialect.EscapeIdentifier));
            var valuesPerRow = 4 + columns.Count;
            var rowsPerChunk = Math.Max(1, MaxParametersPerChunk / valuesPerRow);

            for (var offset = 0; offset < plan.Rows.Count; offset += rowsPerChunk)
            {
                var chunk = plan.Rows.Skip(offset).Take(rowsPerChunk).ToList();
                await using var cmd = connection.CreateCommand();
                var tuples = new List<string>(chunk.Count);
                for (var r = 0; r < chunk.Count; r++)
                {
                    var row = chunk[r];
                    var placeholders = new List<string>(valuesPerRow);
                    void Bind(string name, object? value)
                    {
                        placeholders.Add("@" + name);
                        var p = cmd.CreateParameter();
                        p.ParameterName = name;
                        p.Value = value ?? DBNull.Value;
                        cmd.Parameters.Add(p);
                    }
                    Bind($"r{r}_seq", row.Seq);
                    Bind($"r{r}_op", OpLetter(row.Op).ToString());
                    Bind($"r{r}_grp", row.Group);
                    Bind($"r{r}_cf", row.ConflictOnNoRows);
                    for (var c = 0; c < columns.Count; c++)
                        Bind($"r{r}_{c}", row.Values.TryGetValue(columns[c], out var v) ? v : null);
                    tuples.Add($"({string.Join(",", placeholders)})");
                }
                cmd.CommandText =
                    $"INSERT INTO {dialect.EscapeIdentifier(stagingName)} ({columnList}) VALUES {string.Join(",", tuples)};";
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        private static void BindParameters(DbCommand cmd, IReadOnlyList<SqlParameterInfo> parameters)
        {
            foreach (var parameter in parameters)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = parameter.Name;
                p.Value = parameter.Value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
        }

        private static async Task ExecuteTextAsync(DbConnection connection, string sql, CancellationToken ct)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static bool IsTruthy(object? scalar) => scalar switch
        {
            null or DBNull => false,
            bool b => b,
            _ => Convert.ToInt64(scalar, CultureInfo.InvariantCulture) != 0,
        };
    }
}
