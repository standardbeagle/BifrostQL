using System.Threading;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// A lock-free admission counter enforcing a hard ceiling on a countable resource. The LDAP
    /// front door uses it in two places: once as a process-wide singleton bounding concurrent
    /// connections (<see cref="LdapWireOptions.MaxConnections"/>), and once per connection bounding
    /// the number of simultaneously-outstanding operations
    /// (<see cref="LdapWireOptions.MaxOutstandingOperations"/>) — the same mechanism, two scopes.
    ///
    /// <para><see cref="TryAcquire"/> reserves a slot with an optimistic compare-and-swap (no lock
    /// on the accept / request hot path); <see cref="Release"/>, always from the caller's
    /// <c>finally</c>, returns it. Over the limit, admission fails cleanly (the caller refuses and
    /// closes / rejects) rather than blocking or crashing — fail-closed by construction.</para>
    /// </summary>
    internal sealed class LdapBoundedCounter
    {
        private readonly int _max;
        private int _current;

        public LdapBoundedCounter(int max, string name)
        {
            if (max < 1)
                throw new ArgumentOutOfRangeException(nameof(max), $"ldap {name} must be at least 1.");
            _max = max;
        }

        /// <summary>Current number of held slots (diagnostics / tests).</summary>
        public int Count => Volatile.Read(ref _current);

        /// <summary>Optimistically reserves one slot; returns false at the ceiling without mutating the counter.</summary>
        public bool TryAcquire()
        {
            while (true)
            {
                var observed = Volatile.Read(ref _current);
                if (observed >= _max)
                    return false;
                if (Interlocked.CompareExchange(ref _current, observed + 1, observed) == observed)
                    return true;
                // Lost the CAS race to another caller; re-observe and retry.
            }
        }

        /// <summary>Returns a previously acquired slot. Balancing acquire/release is the caller's contract.</summary>
        public void Release() => Interlocked.Decrement(ref _current);
    }
}
