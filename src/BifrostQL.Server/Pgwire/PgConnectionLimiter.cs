using System.Threading;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Lock-free admission counter enforcing <see cref="PgWireOptions.MaxConnections"/>
    /// across the whole pgwire front door. A single shared instance is consulted by every
    /// connection: <see cref="TryAcquire"/> reserves a slot with an optimistic
    /// compare-and-swap (no lock in the accept hot path), and <see cref="Release"/> — always
    /// called from the connection's <c>finally</c> — returns it. Over the limit, admission
    /// fails cleanly (the caller answers <c>53300 too_many_connections</c> and closes)
    /// rather than blocking or crashing.
    /// </summary>
    internal sealed class PgConnectionLimiter
    {
        private readonly int _max;
        private int _current;

        public PgConnectionLimiter(int maxConnections)
        {
            if (maxConnections < 1)
                throw new ArgumentOutOfRangeException(nameof(maxConnections),
                    "pgwire MaxConnections must be at least 1.");
            _max = maxConnections;
        }

        /// <summary>Current number of admitted connections (for diagnostics/tests).</summary>
        public int Count => Volatile.Read(ref _current);

        /// <summary>
        /// Optimistically reserves one connection slot. Returns false when the limit is
        /// already reached, without mutating the counter. Lock-free CAS loop.
        /// </summary>
        public bool TryAcquire()
        {
            while (true)
            {
                var observed = Volatile.Read(ref _current);
                if (observed >= _max) return false;
                if (Interlocked.CompareExchange(ref _current, observed + 1, observed) == observed)
                    return true;
                // Lost the race to another connection; re-observe and retry.
            }
        }

        /// <summary>Returns a previously acquired slot. Idempotency is the caller's contract.</summary>
        public void Release() => Interlocked.Decrement(ref _current);
    }
}
