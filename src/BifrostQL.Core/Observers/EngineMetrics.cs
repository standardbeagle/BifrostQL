using System;
using System.Collections.Generic;
using System.Threading;

namespace BifrostQL.Core.Observers
{
    /// <summary>
    /// Outcome dimension for an engine request counter. A FINITE enum — the label domain is
    /// bounded by construction, so a request self-metric can never be labeled by exception text,
    /// a table name, a tenant id, or any unbounded value (Prometheus slice-5 criterion 2).
    /// </summary>
    public enum EngineRequestOutcome
    {
        Success,
        Error,
        Denied,
    }

    /// <summary>The operation dimension: a read (query) or a write (mutation). Finite by construction.</summary>
    public enum EngineOperation
    {
        Read,
        Write,
    }

    /// <summary>
    /// The front-door adapter dimension for the active-connection gauge. A FIXED enum — the label
    /// domain is the set of known transports plus <see cref="Other"/>, so the gauge's cardinality is
    /// bounded no matter how many distinct peers connect. A new adapter is added here deliberately;
    /// it can never be a caller-supplied string.
    /// </summary>
    public enum EngineAdapter
    {
        GraphQL,
        OData,
        Grpc,
        Prometheus,
        Pgwire,
        Resp,
        Mcp,
        S3,
        Other,
    }

    /// <summary>
    /// A thread-safe, lock-free registry of Bifrost ENGINE self-metrics — the engine's own
    /// operational health (request outcome/count, SQL and transformer duration, active adapter
    /// connections), kept SEPARATE from the database-derived business series (Prometheus slices 1-4).
    ///
    /// <para>Design (criteria 2 and 3):</para>
    /// <list type="bullet">
    /// <item>Every label dimension is a FINITE enum (<see cref="EngineRequestOutcome"/>,
    /// <see cref="EngineOperation"/>, <see cref="EngineAdapter"/>). The record API takes ONLY enum
    /// values, never a caller-supplied string, so an unbounded/PII label (exception text, table,
    /// tenant, user id, raw SQL) is structurally impossible — bounded cardinality is enforced by the
    /// type system, not by convention.</item>
    /// <item>All accumulators are fixed, pre-allocated arrays indexed by enum ordinal, updated with
    /// <see cref="Interlocked"/> (lock-free). Concurrent requests are safe with no per-record
    /// allocation.</item>
    /// <item><see cref="Enabled"/> gates every record method as its FIRST statement — a single
    /// volatile read. When self-metrics are off, the hot path pays ~nothing and allocates nothing.</item>
    /// </list>
    ///
    /// <para>This is the shared sink read by the pull-based Prometheus exposition at scrape time; it
    /// deliberately does NOT use <c>System.Diagnostics.Metrics</c> (a push/listener model that would
    /// need a MeterListener aggregator to serve a pull scrape — more code, more allocation, identical
    /// output). Lock-free accumulators also match the repo's "prefer lock-free over mutex" rule.</para>
    /// </summary>
    public sealed class EngineMetrics
    {
        /// <summary>
        /// Fixed, stable duration histogram boundaries in SECONDS (criterion 4 — bucket stability).
        /// The exposition adds the implicit <c>+Inf</c> bucket. Never reorder or drop a boundary: a
        /// scrape's bucket set is part of the metric's stable contract.
        /// </summary>
        public static readonly double[] DurationBucketsSeconds =
            { 0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10 };

        private const int OutcomeCount = 3;   // EngineRequestOutcome
        private const int OperationCount = 2; // EngineOperation
        private const int AdapterCount = 9;   // EngineAdapter

        // requests_total[operation, outcome]
        private readonly long[,] _requests = new long[OperationCount, OutcomeCount];

        // one histogram per operation for SQL and transformer durations.
        private readonly Histogram[] _sqlDuration;
        private readonly Histogram[] _transformerDuration;

        // active_connections[adapter]
        private readonly long[] _connections = new long[AdapterCount];

        private volatile bool _enabled;

        public EngineMetrics(bool enabled = false)
        {
            _enabled = enabled;
            _sqlDuration = new Histogram[OperationCount];
            _transformerDuration = new Histogram[OperationCount];
            for (var i = 0; i < OperationCount; i++)
            {
                _sqlDuration[i] = new Histogram(DurationBucketsSeconds.Length);
                _transformerDuration[i] = new Histogram(DurationBucketsSeconds.Length);
            }
        }

        /// <summary>
        /// When false, every record method is a no-op returning before any work — a single volatile
        /// read, zero allocation on the hot path. Flip to true only when a scrape surface is
        /// configured; enabling it is a posture change the host should log.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>Records one completed engine request under its (operation, outcome) counter.</summary>
        public void RecordRequest(EngineOperation operation, EngineRequestOutcome outcome)
        {
            if (!_enabled) return;
            Interlocked.Increment(ref _requests[(int)operation, (int)outcome]);
        }

        /// <summary>Records one SQL execution duration (seconds) into the operation's histogram.</summary>
        public void RecordSqlDuration(EngineOperation operation, double seconds)
        {
            if (!_enabled) return;
            _sqlDuration[(int)operation].Observe(seconds);
        }

        /// <summary>Records one transformer-pipeline duration (seconds) into the operation's histogram.</summary>
        public void RecordTransformerDuration(EngineOperation operation, double seconds)
        {
            if (!_enabled) return;
            _transformerDuration[(int)operation].Observe(seconds);
        }

        /// <summary>Increments the active-connection gauge for an adapter (a wire connection opened).</summary>
        public void ConnectionOpened(EngineAdapter adapter)
        {
            if (!_enabled) return;
            Interlocked.Increment(ref _connections[(int)adapter]);
        }

        /// <summary>Decrements the active-connection gauge for an adapter (a wire connection closed).</summary>
        public void ConnectionClosed(EngineAdapter adapter)
        {
            if (!_enabled) return;
            Interlocked.Decrement(ref _connections[(int)adapter]);
        }

        /// <summary>
        /// Takes a consistent-enough point-in-time reading of every instrument for rendering. Reads
        /// are lock-free (Volatile/Interlocked); a scrape tolerates a skew of at most one in-flight
        /// increment per counter, which cannot produce a negative or unbounded value.
        /// </summary>
        public EngineMetricsSnapshot Snapshot()
        {
            var requests = new List<EngineRequestReading>(OperationCount * OutcomeCount);
            for (var op = 0; op < OperationCount; op++)
                for (var oc = 0; oc < OutcomeCount; oc++)
                {
                    var count = Interlocked.Read(ref _requests[op, oc]);
                    requests.Add(new EngineRequestReading((EngineOperation)op, (EngineRequestOutcome)oc, count));
                }

            var sql = new List<EngineHistogramReading>(OperationCount);
            var transformer = new List<EngineHistogramReading>(OperationCount);
            for (var op = 0; op < OperationCount; op++)
            {
                sql.Add(_sqlDuration[op].Read((EngineOperation)op));
                transformer.Add(_transformerDuration[op].Read((EngineOperation)op));
            }

            var connections = new List<EngineConnectionReading>(AdapterCount);
            for (var a = 0; a < AdapterCount; a++)
            {
                var active = Interlocked.Read(ref _connections[a]);
                connections.Add(new EngineConnectionReading((EngineAdapter)a, active));
            }

            return new EngineMetricsSnapshot(requests, sql, transformer, connections);
        }

        /// <summary>
        /// A fixed-boundary duration histogram: non-cumulative per-bucket counts plus a total count and
        /// a running sum, all updated lock-free. Cumulative <c>le</c> buckets are derived at read time.
        /// </summary>
        private sealed class Histogram
        {
            private readonly long[] _buckets; // length = boundaries + 1 (the +Inf overflow bucket)
            private long _count;
            private double _sum;

            public Histogram(int boundaryCount) => _buckets = new long[boundaryCount + 1];

            public void Observe(double seconds)
            {
                var idx = DurationBucketsSeconds.Length; // default: the +Inf overflow bucket
                for (var i = 0; i < DurationBucketsSeconds.Length; i++)
                {
                    if (seconds <= DurationBucketsSeconds[i]) { idx = i; break; }
                }
                Interlocked.Increment(ref _buckets[idx]);
                Interlocked.Increment(ref _count);
                AddDouble(ref _sum, seconds);
            }

            public EngineHistogramReading Read(EngineOperation operation)
            {
                var cumulative = new List<EngineHistogramBucket>(DurationBucketsSeconds.Length + 1);
                long running = 0;
                for (var i = 0; i < DurationBucketsSeconds.Length; i++)
                {
                    running += Interlocked.Read(ref _buckets[i]);
                    cumulative.Add(new EngineHistogramBucket(DurationBucketsSeconds[i], running));
                }
                running += Interlocked.Read(ref _buckets[DurationBucketsSeconds.Length]);
                cumulative.Add(new EngineHistogramBucket(double.PositiveInfinity, running));

                var count = Interlocked.Read(ref _count);
                var sum = Volatile.Read(ref _sum);
                return new EngineHistogramReading(operation, cumulative, count, sum);
            }

            private static void AddDouble(ref double location, double value)
            {
                double initial, computed;
                do
                {
                    initial = Volatile.Read(ref location);
                    computed = initial + value;
                }
                while (Interlocked.CompareExchange(ref location, computed, initial) != initial);
            }
        }
    }

    /// <summary>One request-counter reading: an (operation, outcome) label pair and its cumulative count.</summary>
    public sealed record EngineRequestReading(EngineOperation Operation, EngineRequestOutcome Outcome, long Count);

    /// <summary>One cumulative histogram bucket: the <c>le</c> upper bound and the count at or below it.</summary>
    public sealed record EngineHistogramBucket(double UpperBound, long CumulativeCount);

    /// <summary>One histogram reading for an operation: cumulative <c>le</c> buckets, total count, and sum (seconds).</summary>
    public sealed record EngineHistogramReading(
        EngineOperation Operation,
        IReadOnlyList<EngineHistogramBucket> Buckets,
        long Count,
        double Sum);

    /// <summary>One active-connection gauge reading: the adapter and its current active count.</summary>
    public sealed record EngineConnectionReading(EngineAdapter Adapter, long Active);

    /// <summary>An immutable point-in-time reading of every engine instrument, for exposition rendering.</summary>
    public sealed record EngineMetricsSnapshot(
        IReadOnlyList<EngineRequestReading> Requests,
        IReadOnlyList<EngineHistogramReading> SqlDurations,
        IReadOnlyList<EngineHistogramReading> TransformerDurations,
        IReadOnlyList<EngineConnectionReading> Connections);
}
