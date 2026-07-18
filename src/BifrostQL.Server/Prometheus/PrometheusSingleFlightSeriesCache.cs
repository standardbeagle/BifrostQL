using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// A per-key single-flight TTL cache for collected Prometheus series — the DoS backstop for the
    /// scrape surface (protocol-adapter-security invariant 6). Within a key's TTL a cached series is
    /// served without touching the database; when a key is cold or stale, N concurrent callers share
    /// ONE in-flight collection (request coalescing) rather than each firing its own aggregate query.
    ///
    /// <para>The cache key is supplied by the caller and MUST partition by identity — a metric's
    /// cached series is only ever served for the exact (endpoint + model + series + security-mode +
    /// identity-partition) key it was collected under, so one identity partition can never read
    /// another's cached series (criterion 2, a cross-tenant cache leak is a BLOCKER-class bug).</para>
    ///
    /// <para>A failed/timed-out collection NEVER populates the cache as fresh: on failure the
    /// in-flight slot is cleared and the last successful value (if any) is preserved, so a caller can
    /// deliberately serve it as STALE via <see cref="TryGetStale"/> — the failure is surfaced by
    /// health self-metrics, never cached as a valid result (criterion 3).</para>
    /// </summary>
    public sealed class PrometheusSingleFlightSeriesCache
    {
        private sealed class Slot
        {
            public Task<PrometheusMetricSeries>? InFlight;
            public PrometheusMetricSeries? LastSuccess;
            public DateTimeOffset FreshUntil;
        }

        private readonly TimeSpan _ttl;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);

        public PrometheusSingleFlightSeriesCache(TimeSpan ttl, Func<DateTimeOffset>? clock = null)
        {
            _ttl = ttl;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Returns the fresh cached series for <paramref name="key"/> if within TTL; otherwise
        /// coalesces onto the single in-flight collection for that key (starting one if none is
        /// running). A successful collection is cached fresh for the TTL; a failure clears only the
        /// in-flight slot and rethrows, leaving any prior success intact for stale serving.
        /// </summary>
        public Task<PrometheusMetricSeries> GetOrCollectAsync(
            string key, Func<CancellationToken, Task<PrometheusMetricSeries>> factory, CancellationToken cancellationToken)
        {
            var slot = _slots.GetOrAdd(key, _ => new Slot());
            lock (slot)
            {
                if (slot.LastSuccess is not null && _clock() < slot.FreshUntil)
                    return Task.FromResult(slot.LastSuccess);

                // A collection is already running for this key — every concurrent caller awaits it.
                if (slot.InFlight is not null)
                    return slot.InFlight;

                var task = RunAsync(slot, factory, cancellationToken);
                slot.InFlight = task;
                return task;
            }
        }

        /// <summary>The last successfully collected series for the key, regardless of TTL (stale).</summary>
        public bool TryGetStale(string key, out PrometheusMetricSeries series)
        {
            if (_slots.TryGetValue(key, out var slot))
            {
                lock (slot)
                {
                    if (slot.LastSuccess is not null)
                    {
                        series = slot.LastSuccess;
                        return true;
                    }
                }
            }

            series = null!;
            return false;
        }

        private async Task<PrometheusMetricSeries> RunAsync(
            Slot slot, Func<CancellationToken, Task<PrometheusMetricSeries>> factory, CancellationToken cancellationToken)
        {
            // Return control to GetOrCollectAsync (which assigns slot.InFlight) BEFORE running the
            // factory, so a synchronously-completing factory cannot clear InFlight before the caller
            // sets it — which would otherwise pin a completed task as "in flight" forever.
            await Task.Yield();
            try
            {
                var result = await factory(cancellationToken).ConfigureAwait(false);
                lock (slot)
                {
                    slot.LastSuccess = result;
                    slot.FreshUntil = _clock() + _ttl;
                    slot.InFlight = null;
                }
                return result;
            }
            catch
            {
                // A failure (timeout, cancellation, DB error) must NOT be cached as fresh — clear the
                // in-flight slot and leave the prior success (if any) for deliberate stale serving.
                lock (slot)
                {
                    slot.InFlight = null;
                }
                throw;
            }
        }
    }
}
