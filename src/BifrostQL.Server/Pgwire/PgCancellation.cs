using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// The shared BackendKeyData ⇄ CancelRequest table for the pgwire front door. Every
    /// authenticated connection publishes a unique <c>(PID, secret)</c> pair here (sent to
    /// the client in BackendKeyData); a later out-of-band CancelRequest — which arrives on a
    /// SEPARATE, unauthenticated connection — looks its target up by PID and only signals
    /// cancellation when the accompanying secret matches.
    ///
    /// <para><b>Fail closed.</b> An unknown PID or a mismatched secret is a silent no-op:
    /// the canceller can never abort a session it does not hold the secret for, and a bogus
    /// CancelRequest never errors or disturbs any live session. Cancellation is advisory /
    /// best-effort, exactly as PostgreSQL specifies.</para>
    /// </summary>
    internal sealed class PgCancellationRegistry
    {
        private readonly ConcurrentDictionary<int, PgCancellationRegistration> _byPid = new();

        /// <summary>
        /// Allocates a unique backend PID + a cryptographically random secret and registers a
        /// fresh <see cref="PgCancellationRegistration"/>. The caller sends the pair in
        /// BackendKeyData and MUST <see cref="Unregister"/> it in its connection's finally.
        /// </summary>
        public PgCancellationRegistration Register()
        {
            while (true)
            {
                // A non-zero, unique key. The value is opaque to the client; uniqueness is all
                // that matters for lookup (real pg uses the backend process id).
                var pid = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                var secret = RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);
                var registration = new PgCancellationRegistration(pid, secret);
                if (_byPid.TryAdd(pid, registration))
                    return registration;
                // Extremely rare PID collision: retry with a new key.
            }
        }

        public void Unregister(PgCancellationRegistration registration)
            => _byPid.TryRemove(new KeyValuePair<int, PgCancellationRegistration>(registration.Pid, registration));

        /// <summary>
        /// Best-effort cancel of the session identified by <paramref name="pid"/>, but only
        /// when <paramref name="secret"/> matches the registered secret. A miss (unknown PID or
        /// wrong secret) does nothing and never throws.
        /// </summary>
        public void TryCancel(int pid, int secret)
        {
            if (_byPid.TryGetValue(pid, out var registration) && registration.SecretEquals(secret))
                registration.RequestCancel();
        }
    }

    /// <summary>
    /// One connection's cancellation slot. Holds the connection's stable <see cref="Pid"/>
    /// and secret, and the cancellation source of the query currently in flight (if any).
    /// The registry (driven by another connection's CancelRequest thread) calls
    /// <see cref="RequestCancel"/>; the owning connection calls
    /// <see cref="BeginQuery"/>/<see cref="EndQuery"/> around each query it runs.
    /// </summary>
    internal sealed class PgCancellationRegistration
    {
        private readonly int _secret;
        private readonly object _gate = new();
        private CancellationTokenSource? _current;
        private bool _cancelRequested;

        public PgCancellationRegistration(int pid, int secret)
        {
            Pid = pid;
            _secret = secret;
        }

        public int Pid { get; }
        public int Secret => _secret;

        /// <summary>Constant-time-agnostic exact match of the CancelRequest secret.</summary>
        public bool SecretEquals(int candidate) => candidate == _secret;

        /// <summary>
        /// Opens a cancellation scope for one query, linked to the connection token, and
        /// returns the token to hand to the executor. Clears any prior cancel flag so a
        /// stale CancelRequest cannot pre-cancel the next query.
        /// </summary>
        public CancellationToken BeginQuery(CancellationToken connectionToken)
        {
            lock (_gate)
            {
                _cancelRequested = false;
                _current = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
                return _current.Token;
            }
        }

        /// <summary>Closes the current query's cancellation scope. Safe to call unmatched.</summary>
        public void EndQuery()
        {
            CancellationTokenSource? toDispose;
            lock (_gate)
            {
                toDispose = _current;
                _current = null;
            }
            toDispose?.Dispose();
        }

        /// <summary>
        /// Signals the in-flight query's token (if any). Best-effort: a disposed source (query
        /// already finished) is swallowed. Sets a flag the owning connection reads to tell a
        /// user cancel (session survives) from a connection teardown.
        /// </summary>
        public void RequestCancel()
        {
            lock (_gate)
            {
                _cancelRequested = true;
                try { _current?.Cancel(); }
                catch (ObjectDisposedException) { /* query finished between lookup and cancel */ }
            }
        }

        /// <summary>True when a CancelRequest matched during the current query's scope.</summary>
        public bool WasCancelRequested
        {
            get { lock (_gate) { return _cancelRequested; } }
        }
    }
}
