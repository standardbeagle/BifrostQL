using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Cdc
{
    /// <summary>
    /// How much of the mutated row is captured into an emitted event payload.
    /// </summary>
    public enum CdcPayloadMode
    {
        /// <summary>Entire post-image of the row.</summary>
        Full,

        /// <summary>Only the changed columns plus the primary key.</summary>
        Changed,

        /// <summary>Primary key columns only.</summary>
        Keys,
    }

    /// <summary>
    /// Durable event sink a table's events are written to. Only the transactional
    /// outbox exists in this slice; webhook / queue delivery is drained FROM the
    /// outbox by the dispatcher (a later CDC sub-task).
    /// </summary>
    public enum CdcEventSink
    {
        /// <summary>Transactional outbox row, written in the same transaction as the data change.</summary>
        Outbox,
    }

    /// <summary>
    /// Parsed per-table Change Data Capture configuration. Built from
    /// <c>emit-events</c> / <c>event-sink</c> / <c>event-payload</c> table metadata.
    /// A table with no <c>emit-events</c> key returns <see cref="None"/>.
    /// </summary>
    public sealed class CdcEventConfig
    {
        /// <summary>The no-events sentinel returned for tables that do not opt in.</summary>
        public static readonly CdcEventConfig None = new(
            Array.Empty<MutationType>(), CdcEventSink.Outbox, CdcPayloadMode.Full);

        private readonly IReadOnlySet<MutationType> _operations;

        private CdcEventConfig(
            IEnumerable<MutationType> operations,
            CdcEventSink sink,
            CdcPayloadMode payload)
        {
            _operations = new HashSet<MutationType>(operations);
            Sink = sink;
            PayloadMode = payload;
        }

        /// <summary>Whether the table emits any events (at least one operation opted in).</summary>
        public bool EmitsEvents => _operations.Count > 0;

        /// <summary>The durable sink events are written to.</summary>
        public CdcEventSink Sink { get; }

        /// <summary>The payload capture mode.</summary>
        public CdcPayloadMode PayloadMode { get; }

        /// <summary>Whether <paramref name="operation"/> emits an event.</summary>
        public bool Emits(MutationType operation) => _operations.Contains(operation);

        /// <summary>
        /// Parses the CDC config for a single table. Follows the per-table
        /// metadata-read pattern used by <see cref="PolicyFilterTransformer"/> /
        /// tenant-filter — a table's event config is fully described by its own
        /// metadata. Throws <see cref="InvalidOperationException"/> on an
        /// unrecognized operation, sink, or payload token so a typo fails fast
        /// rather than silently dropping (and never emitting) an event.
        /// </summary>
        public static CdcEventConfig FromTable(IDbTable table)
        {
            if (table is null)
                throw new ArgumentNullException(nameof(table));

            var emitRaw = table.GetMetadataValue(MetadataKeys.Cdc.EmitEvents);
            if (string.IsNullOrWhiteSpace(emitRaw))
                return None;

            // Materialize operations first so (a) a token error is reported before a
            // secondary sink/payload error, matching the key order the author wrote,
            // and (b) we can enforce the "present but empty" guard below.
            var operations = ParseOperations(emitRaw).ToList();

            // emit-events is present (non-blank) but named no valid operation — e.g.
            // "," or ", ,". RemoveEmptyEntries would leave a zero-length set, which
            // reads as EmitsEvents=false: the table is SILENTLY opted out of the very
            // events it declared. That is the exact fail-open this slice must reject.
            if (operations.Count == 0)
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Cdc.EmitEvents}' is set but names no valid operation. " +
                    $"Provide a comma-separated subset of " +
                    $"{string.Join(", ", Enum.GetNames<MutationType>().Select(n => n.ToLowerInvariant()))}.");

            var sink = ParseSink(table.GetMetadataValue(MetadataKeys.Cdc.EventSink));
            var payload = ParsePayload(table.GetMetadataValue(MetadataKeys.Cdc.EventPayload));

            return new CdcEventConfig(operations, sink, payload);
        }

        private static IEnumerable<MutationType> ParseOperations(string raw)
        {
            foreach (var token in SplitList(raw))
            {
                if (Enum.TryParse<MutationType>(token, ignoreCase: true, out var op))
                {
                    yield return op;
                    continue;
                }

                throw new InvalidOperationException(
                    $"Unknown event operation '{token}' in '{MetadataKeys.Cdc.EmitEvents}'. " +
                    $"Valid operations: {string.Join(", ", Enum.GetNames<MutationType>().Select(n => n.ToLowerInvariant()))}.");
            }
        }

        private static CdcEventSink ParseSink(string? raw)
        {
            // Omitted sink defaults to the outbox — the only durable-transactional
            // sink in this slice and the value implied by opting into emit-events.
            if (string.IsNullOrWhiteSpace(raw))
                return CdcEventSink.Outbox;

            var token = raw.Trim();
            if (string.Equals(token, MetadataKeys.Cdc.SinkOutbox, StringComparison.OrdinalIgnoreCase))
                return CdcEventSink.Outbox;

            throw new InvalidOperationException(
                $"Unknown event sink '{token}' in '{MetadataKeys.Cdc.EventSink}'. " +
                $"Valid sinks: {string.Join(", ", MetadataKeys.Cdc.Sinks)}.");
        }

        private static CdcPayloadMode ParsePayload(string? raw)
        {
            // Omitted payload defaults to the full post-image.
            if (string.IsNullOrWhiteSpace(raw))
                return CdcPayloadMode.Full;

            var token = raw.Trim();
            return token.ToLowerInvariant() switch
            {
                MetadataKeys.Cdc.PayloadFull => CdcPayloadMode.Full,
                MetadataKeys.Cdc.PayloadChanged => CdcPayloadMode.Changed,
                MetadataKeys.Cdc.PayloadKeys => CdcPayloadMode.Keys,
                _ => throw new InvalidOperationException(
                    $"Unknown event payload mode '{token}' in '{MetadataKeys.Cdc.EventPayload}'. " +
                    $"Valid modes: {string.Join(", ", MetadataKeys.Cdc.PayloadModes)}."),
            };
        }

        private static IEnumerable<string> SplitList(string? raw) =>
            (raw ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
