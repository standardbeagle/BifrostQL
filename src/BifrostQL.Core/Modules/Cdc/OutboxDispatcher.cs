using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using GraphQL;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Modules.Cdc
{
    /// <summary>
    /// Drains the transactional outbox (slice 1-3 writer) and delivers each captured
    /// change to a registered <see cref="IEventSink"/> as a CloudEvents 1.0 envelope
    /// (<see cref="CloudEventEnvelope.Build"/>). One monotonic-order polling loop with
    /// jittered exponential backoff on transient sink failure.
    ///
    /// <para>Ownership split mirrors <c>IProtocolAdapter</c>: this engine lives in Core
    /// (no hosting dependency); the host wraps <see cref="RunAsync"/> in an
    /// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> (see
    /// <c>BifrostServiceRegistrar</c>). The sink owns the wire; the dispatcher owns
    /// drain order, <c>dispatched_at</c> stamping, attempt counting, and backoff — a
    /// sink never touches the outbox table.</para>
    ///
    /// <para>Fail-safe: a per-row sink exception is caught, logged server-side, and
    /// converted to a transient failure; the loop never lets an exception escape to the
    /// host, and honours the shutdown <see cref="CancellationToken"/>. A non-CDC host
    /// (no <see cref="MetadataKeys.Cdc.OutboxTable"/> configured) starts and then idles
    /// without polling.</para>
    /// </summary>
    public sealed class OutboxDispatcher
    {
        // Base cadence between drain passes when the queue is empty or fully drained.
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        // Exponential-backoff floor/ceiling applied after a transient sink failure.
        private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
        // Rows read per pass: bounds memory and keeps a stuck row from starving shutdown.
        private const int BatchSize = 100;

        private readonly PathCache<Inputs> _pathCache;
        private readonly IEventSink? _sink;
        private readonly Func<double> _jitter;
        private readonly ILogger? _logger;

        /// <param name="pathCache">The endpoint cache the GraphQL middleware populates; its
        /// first value carries the resolved <c>model</c> and <c>connFactory</c>.</param>
        /// <param name="sink">The delivery target. Null when no sink is registered — the
        /// dispatcher then idles rather than busy-looping over undeliverable rows.</param>
        /// <param name="jitter">Injected randomness in [0,1) for deterministic backoff under
        /// test; defaults to <see cref="Random.Shared"/>.</param>
        public OutboxDispatcher(
            PathCache<Inputs> pathCache,
            IEventSink? sink = null,
            Func<double>? jitter = null,
            ILogger? logger = null)
        {
            _pathCache = pathCache ?? throw new ArgumentNullException(nameof(pathCache));
            _sink = sink;
            _jitter = jitter ?? Random.Shared.NextDouble;
            _logger = logger;
        }

        /// <summary>
        /// The polling loop. Resolves the model/connection lazily each pass, no-ops (stops)
        /// when no outbox is configured, drains one batch per pass in monotonic id order,
        /// and backs off after a transient failure. Runs until <paramref name="cancellationToken"/>
        /// is signalled; never throws to the caller.
        /// </summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delay = PollInterval;
                try
                {
                    var (model, connFactory) = await ResolveAsync();
                    if (model is not null && connFactory is not null)
                    {
                        var outboxName = model.GetMetadataValue(MetadataKeys.Cdc.OutboxTable);
                        if (string.IsNullOrWhiteSpace(outboxName))
                        {
                            // No table opts into CDC on this host: stop the loop entirely so a
                            // non-CDC host pays nothing (no polling, no further DB access).
                            _logger?.LogDebug("CDC outbox dispatcher idle: no '{Key}' configured.",
                                MetadataKeys.Cdc.OutboxTable);
                            return;
                        }

                        if (_sink is null)
                        {
                            _logger?.LogWarning(
                                "CDC outbox is configured ('{Key}') but no IEventSink is registered; " +
                                "the dispatcher is idle. Register a sink to drain events.",
                                MetadataKeys.Cdc.OutboxTable);
                            return;
                        }

                        var outcome = await DrainOnceAsync(model, connFactory, _sink, _logger, BatchSize, cancellationToken);
                        delay = outcome.FailedAttempts is int attempts
                            ? ComputeBackoff(attempts, _jitter(), BaseBackoff, MaxBackoff)
                            : PollInterval;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Fail-safe: a resolution/drain error must not kill the hosted service.
                    _logger?.LogError(ex, "CDC outbox dispatcher drain pass failed; retrying after backoff.");
                }

                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task<(IDbModel? Model, IDbConnFactory? ConnFactory)> ResolveAsync()
        {
            var inputs = await _pathCache.GetFirstValueAsync();
            if (inputs is null)
                return (null, null);

            var model = inputs.TryGetValue("model", out var m) ? m as IDbModel : null;
            var factory = inputs.TryGetValue("connFactory", out var f) ? f as IDbConnFactory : null;
            return (model, factory);
        }

        /// <summary>
        /// One drain pass: reads a batch of undelivered, non-dead outbox rows in monotonic
        /// <see cref="MetadataKeys.Cdc.ColId"/> order and delivers each to the sink. On
        /// success the row is stamped <see cref="MetadataKeys.Cdc.ColDispatchedAt"/>; on the
        /// first transient failure the row's <see cref="MetadataKeys.Cdc.ColAttempts"/> is
        /// incremented, <c>dispatched_at</c> is left null, and the pass STOPS (a later,
        /// higher-id row must not be delivered ahead of a still-pending earlier one). A
        /// per-row exception from the sink or envelope build is caught, logged, and treated
        /// as a transient failure. No-ops when no outbox is configured.
        /// </summary>
        internal static async Task<DrainOutcome> DrainOnceAsync(
            IDbModel model,
            IDbConnFactory connFactory,
            IEventSink sink,
            ILogger? logger,
            int batchSize,
            CancellationToken cancellationToken)
        {
            var outboxName = model.GetMetadataValue(MetadataKeys.Cdc.OutboxTable);
            if (string.IsNullOrWhiteSpace(outboxName))
                return DrainOutcome.Idle; // Non-CDC host: read nothing, poll nothing.

            var outbox = ModelTableReference.Find(model, outboxName);
            if (outbox is null)
            {
                logger?.LogError("Configured CDC outbox table '{Outbox}' was not found in the model.", outboxName);
                return DrainOutcome.Idle;
            }

            var dialect = connFactory.Dialect;
            await using var conn = connFactory.GetConnection();
            await conn.OpenAsync(cancellationToken);

            var rows = await ReadEligibleAsync(conn, dialect, outbox, batchSize, cancellationToken);

            var delivered = 0;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentAttempts = ToInt(row[MetadataKeys.Cdc.ColAttempts]);
                EventDeliveryResult result;
                try
                {
                    var subject = ComputeSubject(model, row);
                    var envelope = CloudEventEnvelope.Build(row, subject);
                    result = await sink.DeliverAsync(envelope, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Never leak payload/exception detail onto a wire; log server-side only.
                    logger?.LogError(ex, "CDC event delivery failed for outbox row id {Id}; will retry.",
                        row[MetadataKeys.Cdc.ColId]);
                    result = EventDeliveryResult.TransientFailure;
                }

                if (result == EventDeliveryResult.Delivered)
                {
                    await StampDispatchedAsync(conn, dialect, outbox, row[MetadataKeys.Cdc.ColId], cancellationToken);
                    delivered++;
                    continue;
                }

                // Transient failure: bump attempts, leave dispatched_at null, stop the pass
                // so ordering holds and the loop backs off before the next attempt.
                await IncrementAttemptsAsync(conn, dialect, outbox, row[MetadataKeys.Cdc.ColId], cancellationToken);
                return new DrainOutcome(delivered, currentAttempts + 1);
            }

            return new DrainOutcome(delivered, null);
        }

        /// <summary>
        /// Bounded, jittered exponential backoff as a pure function of the attempt count.
        /// Equal-jitter: the delay is uniform in <c>[capped/2, capped]</c> where
        /// <c>capped = min(maxDelay, baseDelay·2^(attempts-1))</c> — so it grows
        /// exponentially, is capped, is randomised to avoid thundering herds, and always
        /// keeps a floor (never a tight retry). <paramref name="jitter"/> is injected in
        /// [0,1) so the result is deterministic under test.
        /// </summary>
        internal static TimeSpan ComputeBackoff(int attempts, double jitter, TimeSpan baseDelay, TimeSpan maxDelay)
        {
            if (attempts < 1) attempts = 1;
            // Cap the exponent well below double-overflow; 2^30·1s already dwarfs any sane max.
            var exponent = Math.Min(attempts - 1, 30);
            var scaledMs = baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
            var cappedMs = Math.Min(scaledMs, maxDelay.TotalMilliseconds);
            var half = cappedMs / 2.0;
            var withJitterMs = half + Math.Clamp(jitter, 0.0, 1.0) * half;
            return TimeSpan.FromMilliseconds(withJitterMs);
        }

        private static async Task<List<Dictionary<string, object?>>> ReadEligibleAsync(
            DbConnection conn, ISqlDialect dialect, IDbTable outbox, int batchSize, CancellationToken ct)
        {
            var tableRef = dialect.TableReference(outbox.TableSchema, outbox.DbName);
            var columnList = string.Join(",", MetadataKeys.Cdc.OutboxColumns.Select(dialect.EscapeIdentifier));
            var pagination = dialect.Pagination(
                new[] { dialect.EscapeIdentifier(MetadataKeys.Cdc.ColId) }, offset: 0, limit: batchSize);
            var sql =
                $"SELECT {columnList} FROM {tableRef} " +
                $"WHERE {dialect.EscapeIdentifier(MetadataKeys.Cdc.ColDispatchedAt)} IS NULL " +
                $"AND {dialect.EscapeIdentifier(MetadataKeys.Cdc.ColDead)} = @dead {pagination};";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var deadParam = cmd.CreateParameter();
            deadParam.ParameterName = "@dead";
            deadParam.Value = false;
            cmd.Parameters.Add(deadParam);

            var rows = new List<Dictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                NormalizeCreatedAt(row);
                rows.Add(row);
            }
            return rows;
        }

        private static async Task StampDispatchedAsync(
            DbConnection conn, ISqlDialect dialect, IDbTable outbox, object? id, CancellationToken ct)
        {
            var tableRef = dialect.TableReference(outbox.TableSchema, outbox.DbName);
            var sql =
                $"UPDATE {tableRef} SET {dialect.EscapeIdentifier(MetadataKeys.Cdc.ColDispatchedAt)} = @dispatchedAt " +
                $"WHERE {dialect.EscapeIdentifier(MetadataKeys.Cdc.ColId)} = @id;";
            await MutationCommandExecutor.ExecuteNonQuery(conn, null, sql, new Dictionary<string, object?>
            {
                ["dispatchedAt"] = DateTime.UtcNow,
                ["id"] = id,
            }, cancellationToken: ct);
        }

        private static async Task IncrementAttemptsAsync(
            DbConnection conn, ISqlDialect dialect, IDbTable outbox, object? id, CancellationToken ct)
        {
            var attemptsCol = dialect.EscapeIdentifier(MetadataKeys.Cdc.ColAttempts);
            var tableRef = dialect.TableReference(outbox.TableSchema, outbox.DbName);
            var sql =
                $"UPDATE {tableRef} SET {attemptsCol} = {attemptsCol} + 1 " +
                $"WHERE {dialect.EscapeIdentifier(MetadataKeys.Cdc.ColId)} = @id;";
            await MutationCommandExecutor.ExecuteNonQuery(conn, null, sql, new Dictionary<string, object?>
            {
                ["id"] = id,
            }, cancellationToken: ct);
        }

        /// <summary>
        /// Computes the CloudEvents <c>subject</c> — the row's primary key — from the payload
        /// column, honouring composite keys (all key columns joined, never just the first).
        /// </summary>
        private static string ComputeSubject(IDbModel model, IReadOnlyDictionary<string, object?> row)
        {
            var aggregate = Convert.ToString(row[MetadataKeys.Cdc.ColAggregate], CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(aggregate))
                throw new InvalidOperationException("Outbox row has a blank 'aggregate' column.");

            var table = ModelTableReference.Find(model, aggregate)
                ?? throw new InvalidOperationException($"Outbox row references unknown aggregate '{aggregate}'.");

            var keyColumns = table.KeyColumns.Select(k => k.ColumnName).ToList();
            if (keyColumns.Count == 0)
                throw new InvalidOperationException(
                    $"Aggregate table '{aggregate}' has no primary key; cannot compute event subject.");

            var payloadText = Convert.ToString(row[MetadataKeys.Cdc.ColPayload], CultureInfo.InvariantCulture);
            var payload = JsonNode.Parse(payloadText ?? "null") as JsonObject
                ?? throw new InvalidOperationException($"Outbox payload for '{aggregate}' is not a JSON object.");

            var parts = new List<string>(keyColumns.Count);
            foreach (var column in keyColumns)
            {
                if (!payload.TryGetPropertyValue(column, out var value) || value is null)
                    throw new InvalidOperationException(
                        $"Outbox payload for '{aggregate}' is missing key column '{column}'.");
                parts.Add(value.ToString());
            }
            return string.Join(":", parts);
        }

        // The outbox created_at reads back as a provider-native DateTime on SQL Server but as
        // an ISO-8601 TEXT string on SQLite. CloudEventEnvelope.Build requires a DateTime, so
        // normalise a string here (as UTC — the writer stored UtcNow).
        private static void NormalizeCreatedAt(IDictionary<string, object?> row)
        {
            if (row.TryGetValue(MetadataKeys.Cdc.ColCreatedAt, out var value) && value is string text
                && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                row[MetadataKeys.Cdc.ColCreatedAt] = parsed;
            }
        }

        private static int ToInt(object? value)
            => value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The result of one <see cref="OutboxDispatcher.DrainOnceAsync"/> pass. <see cref="Delivered"/>
    /// counts stamped rows; <see cref="FailedAttempts"/> is the post-increment attempt count of
    /// the row that hit a transient failure (null when the pass had none), which the loop feeds
    /// into <see cref="OutboxDispatcher.ComputeBackoff"/>.
    /// </summary>
    internal readonly record struct DrainOutcome(int Delivered, int? FailedAttempts)
    {
        public static readonly DrainOutcome Idle = new(0, null);
    }
}
