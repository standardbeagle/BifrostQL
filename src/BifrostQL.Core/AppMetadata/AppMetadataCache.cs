using System;
using System.Threading;
using System.Threading.Tasks;

namespace BifrostQL.Core.AppMetadata
{
    /// <summary>
    /// Memoizes the app-metadata overlay load — once, lazily, off the request thread — but,
    /// unlike a bare <see cref="Lazy{T}"/> of a <see cref="Task"/>, does NOT cache a FAULTED
    /// load for the process lifetime.
    ///
    /// <para>A <c>Lazy&lt;Task&lt;AppMetadataModel&gt;&gt;</c> under the default
    /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> caches the first Task it
    /// produces, faulted or not: a transient IO/DB error on the FIRST <c>/_app-metadata</c>
    /// hit would make the endpoint throw for the rest of the process's life. This resets the
    /// memo when the load faults (or is cancelled) so the next request retries, mirroring
    /// <see cref="BifrostQL.Core.Schema.ProfileModelCache"/>'s failed-entry eviction. A
    /// SUCCESSFUL load is still memoized — the overlay is loaded exactly once on the happy path.</para>
    /// </summary>
    public sealed class AppMetadataCache
    {
        private readonly Func<Task<AppMetadataModel>> _load;
        private volatile Lazy<Task<AppMetadataModel>> _lazy;

        public AppMetadataCache(AppMetadataLoader loader)
            : this((loader ?? throw new ArgumentNullException(nameof(loader))).LoadAsync)
        {
        }

        /// <summary>Test/host seam: memoize an arbitrary async load with the same retry-on-fault semantics.</summary>
        public AppMetadataCache(Func<Task<AppMetadataModel>> load)
        {
            _load = load ?? throw new ArgumentNullException(nameof(load));
            _lazy = NewLazy();
        }

        private Lazy<Task<AppMetadataModel>> NewLazy() =>
            new(_load, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// Returns the memoized overlay, loading it on first call. A faulted load is not
        /// cached: the memo is reset so the next call retries, then the fault is rethrown.
        /// </summary>
        public async Task<AppMetadataModel> GetAsync()
        {
            var lazy = _lazy;
            try
            {
                return await lazy.Value.ConfigureAwait(false);
            }
            catch
            {
                // Swap the faulted memo for a fresh one, but only if no concurrent caller
                // already did — so one transient failure poisons neither this request's
                // retry nor a sibling's in-flight successful load.
                Interlocked.CompareExchange(ref _lazy, NewLazy(), lazy);
                throw;
            }
        }
    }
}
