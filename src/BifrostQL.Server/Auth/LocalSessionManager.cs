using System.Security.Claims;
using BifrostQL.Core.Auth;

namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// Server-side lifecycle of local-auth sessions: issue at sign-in, check on every request,
    /// revoke on logout, idle timeout, account change or administrator action. The cookie carries
    /// only a random session id (claim <see cref="LocalAuthClaims.SessionId"/>); the session's
    /// state lives in <see cref="ILocalAuthStateStore"/>, so revoking a row ends that session at
    /// its next request whatever its cookie says.
    /// </summary>
    internal sealed class LocalSessionManager
    {
        // Upper bound on the live sessions read when enforcing MaxConcurrentSessions; sessions past
        // it are left for their own idle timeout or expiry.
        private const int MaxSessionsScanned = 1_000;
        private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);
        private static readonly TimeSpan PurgeGrace = TimeSpan.FromDays(1);

        private readonly ILocalAuthStateStore _state;
        private readonly LocalAuthAudit _audit;
        private readonly LocalAuthOptions _options;
        private long _nextPurgeTicks;

        public LocalSessionManager(ILocalAuthStateStore state, LocalAuthAudit audit, LocalAuthOptions options)
        {
            _state = state;
            _audit = audit;
            _options = options;
        }

        /// <summary>Records a new session for a verified sign-in and returns the id its cookie carries.</summary>
        public async Task<string> IssueAsync(LocalAccountState account, string? sourceIp, DateTimeOffset now, CancellationToken ct)
        {
            await PurgeIfDueAsync(now, ct).ConfigureAwait(false);

            var sessionId = LocalSessionId.New();
            await _state.CreateSessionAsync(new LocalSession(
                LocalSessionId.Hash(sessionId), account.UserId, now, now, now + _options.SessionLifetime,
                RevokedAt: null, RevokeReason: null, sourceIp, account.CredentialStamp), ct).ConfigureAwait(false);

            if (_options.MaxConcurrentSessions is { } cap)
            {
                var live = await _state.ListLiveSessionsAsync(account.UserId, now, MaxSessionsScanned, ct).ConfigureAwait(false);
                foreach (var oldest in live.Take(Math.Max(0, live.Count - cap)))
                {
                    if (await _state.RevokeSessionAsync(oldest.SessionHash, now, LocalSessionRevokeReasons.ConcurrentSessionLimit, ct)
                            .ConfigureAwait(false))
                        await _audit.RecordAsync(now, AuthAuditKind.SessionRevoked, AuthAuditOutcome.Success,
                            account.UserId, null, sourceIp, LocalSessionRevokeReasons.ConcurrentSessionLimit, ct).ConfigureAwait(false);
                }
            }
            return sessionId;
        }

        /// <summary>
        /// Checks the session a request presents. Returns the live session, or null after revoking it
        /// when it has gone idle. A missing, revoked or expired session is null with no write.
        /// </summary>
        public async Task<LocalSession?> CheckAsync(string sessionId, string? sourceIp, DateTimeOffset now, CancellationToken ct)
        {
            var session = await _state.GetSessionAsync(LocalSessionId.Hash(sessionId), ct).ConfigureAwait(false);
            if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now)
                return null;

            if (now - session.LastSeenAt >= _options.SessionIdleTimeout)
            {
                await RevokeAsync(session, LocalSessionRevokeReasons.IdleTimeout, actorId: null, sourceIp, now, ct).ConfigureAwait(false);
                return null;
            }

            if (now - session.LastSeenAt >= _options.SessionTouchInterval)
                await _state.TouchSessionAsync(session.SessionHash, now, ct).ConfigureAwait(false);
            return session;
        }

        /// <summary>Revokes one session (logout) and audits it. A session already ended records nothing.</summary>
        public async Task LogoutAsync(string sessionId, string? sourceIp, DateTimeOffset now, CancellationToken ct)
        {
            var session = await _state.GetSessionAsync(LocalSessionId.Hash(sessionId), ct).ConfigureAwait(false);
            if (session is null || session.RevokedAt is not null)
                return;
            if (await _state.RevokeSessionAsync(session.SessionHash, now, LocalSessionRevokeReasons.Logout, ct).ConfigureAwait(false))
                await _audit.RecordAsync(now, AuthAuditKind.Logout, AuthAuditOutcome.Success,
                    session.UserId, null, sourceIp, null, ct).ConfigureAwait(false);
        }

        /// <summary>Revokes every live session of <paramref name="userId"/> and audits the revocation.</summary>
        public async Task<int> RevokeAllAsync(
            string userId, string reason, string? actorId, string? sourceIp, DateTimeOffset now, CancellationToken ct)
        {
            var revoked = await _state.RevokeUserSessionsAsync(userId, now, reason, ct).ConfigureAwait(false);
            await _audit.RecordAsync(now, AuthAuditKind.SessionRevoked, AuthAuditOutcome.Success,
                userId, actorId, sourceIp, reason, ct).ConfigureAwait(false);
            return revoked;
        }

        private async Task RevokeAsync(
            LocalSession session, string reason, string? actorId, string? sourceIp, DateTimeOffset now, CancellationToken ct)
        {
            if (await _state.RevokeSessionAsync(session.SessionHash, now, reason, ct).ConfigureAwait(false))
                await _audit.RecordAsync(now, AuthAuditKind.SessionRevoked, AuthAuditOutcome.Success,
                    session.UserId, actorId, sourceIp, reason, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Deletes session rows that ended more than a day ago, at most once an hour per process. The
        /// audit trail, not the sessions table, is the record of who signed in when.
        /// </summary>
        private async Task PurgeIfDueAsync(DateTimeOffset now, CancellationToken ct)
        {
            var due = Interlocked.Read(ref _nextPurgeTicks);
            if (now.UtcTicks < due)
                return;
            if (Interlocked.CompareExchange(ref _nextPurgeTicks, (now + PurgeEvery).UtcTicks, due) != due)
                return;
            await _state.PurgeSessionsExpiredBeforeAsync(now - PurgeGrace, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Administrative actions on local-auth accounts for host code and the optional
    /// <see cref="LocalAuthOptions.AdminPath"/> endpoints. Each action is audited with the acting
    /// administrator as <see cref="AuthAuditEvent.ActorId"/>.
    /// </summary>
    public sealed class LocalAuthAdministration
    {
        private readonly LocalSessionManager _sessions;
        private readonly TimeProvider _time;

        internal LocalAuthAdministration(LocalSessionManager sessions, TimeProvider time)
        {
            _sessions = sessions;
            _time = time;
        }

        /// <summary>Revokes every live session of <paramref name="userId"/>; returns how many were live.</summary>
        public Task<int> RevokeAllSessionsAsync(string userId, string? actorId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userId);
            return _sessions.RevokeAllAsync(userId, LocalSessionRevokeReasons.Administrator, actorId, sourceIp: null,
                _time.GetUtcNow(), cancellationToken);
        }
    }
}
