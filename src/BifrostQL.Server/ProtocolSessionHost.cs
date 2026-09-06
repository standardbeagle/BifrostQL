using BifrostQL.Server.Ldap;
using BifrostQL.Server.Pgwire;
using BifrostQL.Server.Resp;

namespace BifrostQL.Server
{
    /// <summary>
    /// The pre-auth deadline of ONE admitted connection: invariant 15's rule, in one place.
    ///
    /// <para>The admission slot is reserved at ACCEPT, so a peer that never authenticates must not
    /// be able to hold it. A silent socket costs no credentials and no bytes, which makes the
    /// deadline — not the idle timeout — the thing that reclaims the slot: failing binds and
    /// unauthenticated pings keep a connection non-idle indefinitely.</para>
    ///
    /// <para><b>Retire, never re-arm.</b> Only a CREDENTIALED action may clear the deadline: an
    /// authenticated session is a legitimate pooled client and lives under its front door's idle
    /// timeout instead. Every FREE action — an anonymous bind, an unauthenticated ping, StartTLS,
    /// version negotiation, a cancel request — leaves an armed deadline exactly where it is. None
    /// of them is rate limited and none proves who sent it, so a deadline any of them could move
    /// would be renewable at will, which is the same as no deadline. That asymmetry is why
    /// <see cref="RetireOnCredentialedAction"/> and <see cref="ReArmOnReturnToAnonymous"/> are
    /// separate operations and why the latter is a no-op on an already-armed deadline: the only
    /// legal re-arm is the transition BACK to anonymous (a previously credentialed session whose
    /// re-bind failed, RFC 4511 §4.2.1), which bounds that state rather than leaving it unbounded
    /// because it was once authenticated.</para>
    ///
    /// <para><b>One owned timer.</b> The deadline is a single
    /// <see cref="CancellationTokenSource"/> constructed with the timeout and a
    /// <see cref="TimeProvider"/>, linked to the connection token and disposed with the session.
    /// A detached <c>Task.Delay</c> loop that cancels a <c>using</c> source instead outlives every
    /// connection that ends early — it wakes to call <c>Cancel()</c> on a disposed source, and it
    /// keeps a timer plus a task alive per connection for the full timeout, unbounded by
    /// MaxConnections because the slot was already released.</para>
    ///
    /// <para><b>Two shapes, one rule.</b> A straight-line handshake (pgwire) consumes the deadline
    /// as a <see cref="Token"/> that cancels the in-flight read; a message loop (RESP, LDAP)
    /// consumes it as a per-read <see cref="ReadBudget"/> — the SHRINKING remainder of the one
    /// budget, never a fresh timeout per read, or a chatty peer resets it forever.</para>
    /// </summary>
    internal sealed class ProtocolPreAuthDeadline : IDisposable
    {
        private readonly TimeSpan _timeout;
        private readonly TimeProvider _timeProvider;
        /// <summary>Owned by this deadline, disposed with it; null when the timeout is infinite.</summary>
        private readonly CancellationTokenSource? _timer;
        private readonly CancellationTokenSource _linked;
        /// <summary>Null once retired (or never armed): the deadline no longer applies.</summary>
        private DateTimeOffset? _expiresAt;

        internal ProtocolPreAuthDeadline(TimeSpan timeout, TimeProvider timeProvider, CancellationToken connectionToken)
        {
            _timeout = timeout;
            _timeProvider = timeProvider;
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                // No pre-auth phase to bound (a front door configured to require no
                // authentication). The session still cancels with the connection.
                _linked = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
            }
            else
            {
                _timer = new CancellationTokenSource(timeout, timeProvider);
                _expiresAt = timeProvider.GetUtcNow() + timeout;
                _linked = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, _timer.Token);
            }
        }

        /// <summary>
        /// Cancels when the connection closes or the pre-auth deadline expires. Handshake reads
        /// run under this; the post-authentication loop runs under the connection token alone.
        /// </summary>
        public CancellationToken Token => _linked.Token;

        /// <summary>Whether a deadline is currently in force.</summary>
        public bool IsArmed => _expiresAt is not null;

        /// <summary>
        /// The read budget for one message-loop iteration, or null for no deadline.
        ///
        /// <para>A NON-POSITIVE result means the one cumulative pre-auth budget is spent and the
        /// caller must drop the connection.</para>
        /// </summary>
        /// <param name="pastHandshake">
        /// True once this session is authenticated — or, on a front door that requires no
        /// authentication, from the start: such a session never becomes authenticated, so keying
        /// the budget off the flag alone would drop every connection one pre-auth timeout after
        /// connect (H11). A session past the handshake lives under <paramref name="idleTimeout"/>,
        /// which an active client resets on every command.
        /// </param>
        /// <param name="clampToIdleWhileArmed">
        /// Whether the idle timeout ALSO bounds a read taken while the deadline is armed (LDAP:
        /// the loop waits no longer than the shorter of the two). False leaves an unauthenticated
        /// read bounded only by the pre-auth budget (RESP), so an infinite pre-auth timeout there
        /// means no deadline at all rather than an idle one.
        /// </param>
        public TimeSpan? ReadBudget(bool pastHandshake, TimeSpan idleTimeout, bool clampToIdleWhileArmed)
        {
            var idle = idleTimeout == Timeout.InfiniteTimeSpan ? (TimeSpan?)null : idleTimeout;
            if (pastHandshake)
                return idle;
            if (_expiresAt is not { } expires)
                return clampToIdleWhileArmed ? idle : null;
            var remaining = expires - _timeProvider.GetUtcNow();
            if (clampToIdleWhileArmed && idle is { } cap && cap < remaining)
                return cap;
            return remaining;
        }

        /// <summary>
        /// Retires the deadline. The ONLY caller is an action that cost the peer credentials — a
        /// credentialed bind, a completed password/SCRAM exchange. Free actions must not call it.
        /// </summary>
        public void RetireOnCredentialedAction()
        {
            _expiresAt = null;
            // Stand the owned timer down rather than leaving it to fire on an authenticated
            // session. Disposal still happens with the session.
            _timer?.CancelAfter(Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Arms a fresh bounded window on the transition BACK to anonymous, so that state is
        /// bounded rather than unbounded-because-previously-authenticated. Deliberately a NO-OP
        /// while a deadline is already armed: moving an armed deadline is the renewable-window
        /// fail-open, and the caller cannot distinguish a free action from a credential-less
        /// re-bind reliably enough to be trusted with that.
        /// </summary>
        public void ReArmOnReturnToAnonymous()
        {
            if (_expiresAt is not null || _timeout == Timeout.InfiniteTimeSpan)
                return;
            _expiresAt = _timeProvider.GetUtcNow() + _timeout;
            _timer?.CancelAfter(_timeout);
        }

        public void Dispose()
        {
            _linked.Dispose();
            _timer?.Dispose();
        }
    }

    /// <summary>
    /// The admitted-session lifecycle every raw-wire front door shares: ACQUIRE a slot, ARM the
    /// pre-auth deadline, RUN the session, RELEASE the slot.
    ///
    /// <para>pgwire, RESP and LDAP each open a listening port that an unauthenticated peer can
    /// reach, and each owes the same two guarantees on it — a cap counted at accept
    /// (<see cref="ProtocolConnectionLimiter"/>) and a pre-auth deadline
    /// (<see cref="ProtocolPreAuthDeadline"/>). Both were re-implemented per adapter; the
    /// deadline copies drifted apart three times, and each drift failed open.</para>
    ///
    /// <para>Each front door derives its OWN sealed subtype, for the same reason the admission
    /// counter does: registering the base type from two adapters would hand them one instance and
    /// therefore one budget. The subtype is also where its options type is read, so the configured
    /// pre-auth timeout is read in exactly one file — which
    /// <c>ProtocolPreAuthDeadlineUnificationTests</c> asserts.</para>
    /// </summary>
    internal abstract class ProtocolSessionHost
    {
        private readonly TimeSpan _preAuthTimeout;
        private readonly TimeProvider _timeProvider;

        protected ProtocolSessionHost(
            ProtocolConnectionLimiter limiter, TimeSpan preAuthTimeout, TimeProvider? timeProvider)
        {
            Limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
            _preAuthTimeout = preAuthTimeout;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>This front door's admission counter, shared by every listener serving it.</summary>
        public ProtocolConnectionLimiter Limiter { get; }

        /// <summary>The configured cap, for the refusal this front door answers with.</summary>
        public int MaxConnections => Limiter.Max;

        /// <summary>
        /// Acquire → arm → run → release. <paramref name="onRefused"/> writes this protocol's
        /// "too many connections" answer and is called INSTEAD of the session when the cap is
        /// already reached, so an over-cap peer is turned away before any read, TLS handshake or
        /// credential lookup.
        /// </summary>
        public async Task RunAsync(
            CancellationToken connectionToken,
            Func<ProtocolPreAuthDeadline, Task> runSession,
            Func<Task> onRefused)
        {
            if (!Limiter.TryAcquire())
            {
                await onRefused().ConfigureAwait(false);
                return;
            }
            try
            {
                await RunAdmittedAsync(connectionToken, runSession).ConfigureAwait(false);
            }
            finally
            {
                Limiter.Release();
            }
        }

        /// <summary>
        /// Arm → run, for a caller that already took the slot at ACCEPT — listener middleware
        /// ahead of a TLS handshake (RESP's Kestrel path, LDAPS), which is earlier than this
        /// method could. Taking it again here would halve the cap; skipping the deadline would
        /// leave exactly those connections — the ones that reached a TLS state machine — unbounded.
        /// </summary>
        public async Task RunAdmittedAsync(
            CancellationToken connectionToken,
            Func<ProtocolPreAuthDeadline, Task> runSession)
        {
            using var deadline = new ProtocolPreAuthDeadline(_preAuthTimeout, _timeProvider, connectionToken);
            await runSession(deadline).ConfigureAwait(false);
        }
    }

    /// <summary>Session lifecycle for the pgwire listener (<c>PgWireOptions.HandshakeTimeout</c>).</summary>
    internal sealed class PgwireSessionHost : ProtocolSessionHost
    {
        public PgwireSessionHost(
            PgWireOptions options, PgwireConnectionLimiter? limiter = null, TimeProvider? timeProvider = null)
            : base(limiter ?? new PgwireConnectionLimiter(options.MaxConnections),
                   options.HandshakeTimeout, timeProvider)
        { }
    }

    /// <summary>Session lifecycle for the RESP listener (<c>RespWireOptions.AuthenticationTimeout</c>).</summary>
    internal sealed class RespSessionHost : ProtocolSessionHost
    {
        public RespSessionHost(
            RespWireOptions options, RespConnectionLimiter? limiter = null, TimeProvider? timeProvider = null)
            : base(limiter ?? new RespConnectionLimiter(options.MaxConnections),
                   options.AuthenticationTimeout, timeProvider)
        { }
    }

    /// <summary>
    /// Session lifecycle for the LDAP front door (<c>LdapWireOptions.AuthenticationTimeout</c>).
    /// ONE instance serves both the cleartext listener and LDAPS: MaxConnections bounds this front
    /// door's total concurrent connections, so opening a second port must not double the ceiling.
    /// </summary>
    internal sealed class LdapSessionHost : ProtocolSessionHost
    {
        public LdapSessionHost(
            LdapWireOptions options, LdapConnectionLimiter? limiter = null, TimeProvider? timeProvider = null)
            : base(limiter ?? new LdapConnectionLimiter(options.MaxConnections),
                   options.AuthenticationTimeout, timeProvider)
        { }
    }
}
