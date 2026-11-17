using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// <see cref="ILocalAuthStateStore"/> held in process memory. <b>For tests and single-process
    /// development only.</b> Sessions, lockouts, password history and the audit trail are lost on
    /// restart and are not shared between nodes, so a restart silently ends every lockout and
    /// erases the audit trail: no compliance control holds on this store. Production deployments
    /// use <see cref="DbLocalAuthStateStore"/> (the default <c>AddBifrostLocalAuth</c> registers).
    ///
    /// <para>The audit trail is capped at <see cref="MaxAuditEvents"/>; past it every append throws,
    /// because dropping audit events silently is worse than failing the request.</para>
    /// </summary>
    public sealed class InMemoryLocalAuthStateStore : ILocalAuthStateStore
    {
        /// <summary>Most audit events the in-memory trail holds before appends fail.</summary>
        public const int MaxAuditEvents = 100_000;

        private readonly ConcurrentDictionary<string, LocalSession> _sessions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, LockoutRow> _lockouts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ImmutableList<LocalPasswordHistoryEntry>> _history = new(StringComparer.Ordinal);
        private ImmutableList<AuthAuditEvent> _audit = ImmutableList<AuthAuditEvent>.Empty;

        private sealed record LockoutRow(LocalLockoutState State, int Version);

        public Task CreateSessionAsync(LocalSession session, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(session);
            if (!_sessions.TryAdd(session.SessionHash, session))
                throw new InvalidOperationException("A session with this id already exists.");
            return Task.CompletedTask;
        }

        public Task<LocalSession?> GetSessionAsync(string sessionHash, CancellationToken cancellationToken)
            => Task.FromResult(_sessions.TryGetValue(sessionHash, out var s) ? s : null);

        public Task TouchSessionAsync(string sessionHash, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
        {
            Update(sessionHash, s => s.RevokedAt is null && s.LastSeenAt < lastSeenAt ? s with { LastSeenAt = lastSeenAt } : null);
            return Task.CompletedTask;
        }

        public Task<bool> RevokeSessionAsync(string sessionHash, DateTimeOffset revokedAt, string reason, CancellationToken cancellationToken)
            => Task.FromResult(Update(sessionHash, s => s.RevokedAt is null ? s with { RevokedAt = revokedAt, RevokeReason = reason } : null));

        public Task<int> RevokeUserSessionsAsync(string userId, DateTimeOffset revokedAt, string reason, CancellationToken cancellationToken)
        {
            var revoked = _sessions.Values
                .Where(s => s.UserId == userId)
                .Count(s => Update(s.SessionHash, c => c.RevokedAt is null ? c with { RevokedAt = revokedAt, RevokeReason = reason } : null));
            return Task.FromResult(revoked);
        }

        public Task<IReadOnlyList<LocalSession>> ListLiveSessionsAsync(string userId, DateTimeOffset now, int maxRows, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
            IReadOnlyList<LocalSession> live = _sessions.Values
                .Where(s => s.UserId == userId && s.RevokedAt is null && s.ExpiresAt > now)
                .OrderBy(s => s.CreatedAt).ThenBy(s => s.SessionHash, StringComparer.Ordinal)
                .Take(maxRows)
                .ToList();
            return Task.FromResult(live);
        }

        public Task PurgeSessionsExpiredBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            foreach (var s in _sessions.Values.Where(s => s.ExpiresAt < cutoff))
                _sessions.TryRemove(new KeyValuePair<string, LocalSession>(s.SessionHash, s));
            return Task.CompletedTask;
        }

        public Task<LocalLockoutState> GetLockoutAsync(string userId, CancellationToken cancellationToken)
            => Task.FromResult(_lockouts.TryGetValue(userId, out var row) ? row.State : LocalLockoutState.None);

        public Task<LocalFailedLoginResult> RecordFailedLoginAsync(
            string userId, DateTimeOffset at, int threshold, DateTimeOffset lockedUntil, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
            while (true)
            {
                _lockouts.TryGetValue(userId, out var current);
                var state = current?.State ?? LocalLockoutState.None;
                if (state.IsLockedAt(at))
                    return Task.FromResult(new LocalFailedLoginResult(state, LockedNow: false));

                var count = state.FailedCount + 1;
                var locks = count >= threshold;
                var next = new LockoutRow(
                    locks ? new LocalLockoutState(0, lockedUntil) : new LocalLockoutState(count, null),
                    (current?.Version ?? 0) + 1);
                var swapped = current is null
                    ? _lockouts.TryAdd(userId, next)
                    : _lockouts.TryUpdate(userId, next, current);
                if (swapped)
                    return Task.FromResult(new LocalFailedLoginResult(next.State, locks));
            }
        }

        public Task ClearLockoutAsync(string userId, CancellationToken cancellationToken)
        {
            _lockouts.TryRemove(userId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalPasswordHistoryEntry>> GetPasswordHistoryAsync(string userId, int maxEntries, CancellationToken cancellationToken)
        {
            IReadOnlyList<LocalPasswordHistoryEntry> entries = maxEntries < 1 || !_history.TryGetValue(userId, out var list)
                ? Array.Empty<LocalPasswordHistoryEntry>()
                : list.Take(maxEntries).ToList();
            return Task.FromResult(entries);
        }

        public Task AppendPasswordHistoryAsync(string userId, string passwordHash, DateTimeOffset changedAt, int keep, CancellationToken cancellationToken)
        {
            var entry = new LocalPasswordHistoryEntry(passwordHash, changedAt);
            _history.AddOrUpdate(userId,
                _ => ImmutableList.Create(entry),
                (_, list) => list.Insert(0, entry).Take(Math.Max(keep, 1)).ToImmutableList());
            return Task.CompletedTask;
        }

        public Task AppendAuditAsync(AuthAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(auditEvent);
            ImmutableInterlocked.Update(ref _audit, list =>
            {
                if (list.Count >= MaxAuditEvents)
                    throw new InvalidOperationException(
                        $"The in-memory audit trail holds its cap of {MaxAuditEvents} events; use DbLocalAuthStateStore.");
                return list.Add(auditEvent);
            });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuthAuditEvent>> ReadAuditAsync(DateTimeOffset since, int maxRows, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
            IReadOnlyList<AuthAuditEvent> events = Volatile.Read(ref _audit)
                .Where(e => e.OccurredAt >= since)
                .OrderBy(e => e.OccurredAt)
                .Take(maxRows)
                .ToList();
            return Task.FromResult(events);
        }

        /// <summary>Applies <paramref name="change"/> with compare-and-swap; null from it means "no change".</summary>
        private bool Update(string sessionHash, Func<LocalSession, LocalSession?> change)
        {
            while (_sessions.TryGetValue(sessionHash, out var current))
            {
                var next = change(current);
                if (next is null)
                    return false;
                if (_sessions.TryUpdate(sessionHash, next, current))
                    return true;
            }
            return false;
        }
    }
}
