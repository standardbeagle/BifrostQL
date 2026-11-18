using System.Globalization;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// <see cref="ILocalAuthStateStore"/> over four Bifrost-owned tables in the app-user database,
    /// provisioned on first use with the dialect's portable <c>CREATE TABLE IF NOT EXISTS</c>
    /// (SQL Server guards with <c>OBJECT_ID</c>), the same way <c>DbSavedObjectStore</c> provisions
    /// its table. Works on every dialect Bifrost ships: SQL Server, PostgreSQL, MySQL, SQLite.
    ///
    /// <list type="bullet">
    /// <item><c>_bifrost_auth_sessions</c>: one row per issued session, keyed by the SHA-256 of its id.</item>
    /// <item><c>_bifrost_auth_lockouts</c>: one row per account with a failure on record.</item>
    /// <item><c>_bifrost_auth_password_history</c>: password hashes users set through local auth.</item>
    /// <item><c>_bifrost_auth_audit</c>: the authentication audit trail. Append-only from Bifrost's
    /// side; retention and archiving are the deployment's.</item>
    /// </list>
    ///
    /// <para>Timestamps are stored as fixed-width ISO-8601 UTC text (<c>O</c> format), so they compare
    /// and sort correctly as strings on every engine without a dialect-specific date type. Every value
    /// is a parameter and every identifier is dialect-escaped. The lockout counter is updated with a
    /// version-conditioned UPDATE in a retry loop, so two concurrent failures cannot both read the
    /// same count and lose one.</para>
    ///
    /// <para>No secondary indexes are created. Revoking all of a user's sessions scans the sessions
    /// table; a deployment with many sessions should add an index on <c>user_id</c>.</para>
    /// </summary>
    public sealed class DbLocalAuthStateStore : ILocalAuthStateStore
    {
        // Every name carries BifrostSystemTables.AuthStatePrefix, so a GraphQL endpoint serving the
        // same database files these tables as hidden and never exposes them, except through
        // _rawQuery, which runs caller SQL and is opt-in and role-gated (see BifrostSystemTables).
        public const string SessionsTable = BifrostSystemTables.AuthStatePrefix + "sessions";
        public const string LockoutsTable = BifrostSystemTables.AuthStatePrefix + "lockouts";
        public const string PasswordHistoryTable = BifrostSystemTables.AuthStatePrefix + "password_history";
        public const string AuditTable = BifrostSystemTables.AuthStatePrefix + "audit";

        private const int TimeoutSeconds = 30;
        private const int MaxOptimisticAttempts = 16;

        private static readonly IReadOnlyList<SqlColumnDefinition> SessionColumns = new[]
        {
            new SqlColumnDefinition("session_hash", SqlColumnKind.KeyText, Nullable: false, PrimaryKey: true),
            new SqlColumnDefinition("user_id", SqlColumnKind.KeyText, Nullable: false),
            new SqlColumnDefinition("created_at", SqlColumnKind.KeyText, Nullable: false),
            new SqlColumnDefinition("last_seen_at", SqlColumnKind.KeyText, Nullable: false),
            new SqlColumnDefinition("expires_at", SqlColumnKind.KeyText, Nullable: false),
            new SqlColumnDefinition("revoked_at", SqlColumnKind.KeyText, Nullable: true),
            new SqlColumnDefinition("revoke_reason", SqlColumnKind.KeyText, Nullable: true),
            new SqlColumnDefinition("source_ip", SqlColumnKind.KeyText, Nullable: true),
            new SqlColumnDefinition("credential_stamp", SqlColumnKind.KeyText, Nullable: false),
        };

        private static readonly IReadOnlyList<SqlColumnDefinition> LockoutColumns = new[]
        {
            new SqlColumnDefinition("user_id", SqlColumnKind.KeyText, Nullable: false, PrimaryKey: true),
            new SqlColumnDefinition("failed_count", SqlColumnKind.Int, Nullable: false),
            new SqlColumnDefinition("locked_until", SqlColumnKind.KeyText, Nullable: true),
            new SqlColumnDefinition("last_failure_at", SqlColumnKind.KeyText, Nullable: true),
            new SqlColumnDefinition("version", SqlColumnKind.Int, Nullable: false),
        };

        private static readonly IReadOnlyList<SqlColumnDefinition> PasswordHistoryColumns = new[]
        {
            new SqlColumnDefinition("entry_id", SqlColumnKind.KeyText, Nullable: false, PrimaryKey: true),
            new SqlColumnDefinition("user_id", SqlColumnKind.KeyText, Nullable: false),
            new SqlColumnDefinition("changed_at", SqlColumnKind.KeyText, Nullable: false),
            // Per-user insertion order. changed_at is a clock reading, so two changes in one tick
            // (or under a fixed test clock) tie on it; history order and trimming use seq instead.
            new SqlColumnDefinition("seq", SqlColumnKind.Int, Nullable: false),
            new SqlColumnDefinition("password_hash", SqlColumnKind.Text, Nullable: false),
        };

        private static readonly IReadOnlyList<SqlColumnDefinition> AuditColumns = new[]
        {
            new SqlColumnDefinition("event_id", SqlColumnKind.KeyText, Nullable: false, PrimaryKey: true),
            new SqlColumnDefinition("occurred_at", SqlColumnKind.KeyText, Nullable: false),
            new SqlColumnDefinition("kind", SqlColumnKind.KeyText, Nullable: false),
            new SqlColumnDefinition("outcome", SqlColumnKind.KeyText, Nullable: false),
            new SqlColumnDefinition("subject_id", SqlColumnKind.KeyText, Nullable: true),
            new SqlColumnDefinition("actor_id", SqlColumnKind.KeyText, Nullable: true),
            new SqlColumnDefinition("source_ip", SqlColumnKind.KeyText, Nullable: true),
            new SqlColumnDefinition("reason", SqlColumnKind.KeyText, Nullable: true),
        };

        private readonly IDbConnFactory _connFactory;
        private readonly ISqlDialect _dialect;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private volatile bool _initialized;

        public DbLocalAuthStateStore(IDbConnFactory connFactory)
        {
            _connFactory = connFactory ?? throw new ArgumentNullException(nameof(connFactory));
            _dialect = connFactory.Dialect;
        }

        private string Table(string name) => _dialect.TableReference(null, name);
        private string Col(string name) => _dialect.EscapeIdentifier(name);

        public Task EnsureProvisionedAsync(CancellationToken cancellationToken) => EnsureTablesAsync(cancellationToken);

        private async Task EnsureTablesAsync(CancellationToken cancellationToken)
        {
            if (_initialized)
                return;
            await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_initialized)
                    return;
                foreach (var (name, columns) in new[]
                         {
                             (SessionsTable, SessionColumns), (LockoutsTable, LockoutColumns),
                             (PasswordHistoryTable, PasswordHistoryColumns), (AuditTable, AuditColumns),
                         })
                {
                    await ExecuteAsync(_dialect.CreateTableIfNotExistsSql(Table(name), columns), null, 0, cancellationToken)
                        .ConfigureAwait(false);
                }
                _initialized = true;
            }
            finally
            {
                _initLock.Release();
            }
        }

        private Task<RawSqlResult> ExecuteAsync(
            string sql, IReadOnlyDictionary<string, object?>? parameters, int maxRows, CancellationToken cancellationToken)
            => RawSqlExecutor.ExecuteAsync(_connFactory, sql, parameters, TimeoutSeconds, maxRows, cancellationToken);

        private async Task<RawSqlResult> RunAsync(
            string sql, IReadOnlyDictionary<string, object?>? parameters, int maxRows, CancellationToken cancellationToken)
        {
            await EnsureTablesAsync(cancellationToken).ConfigureAwait(false);
            return await ExecuteAsync(sql, parameters, maxRows, cancellationToken).ConfigureAwait(false);
        }

        private string SessionSelect =>
            $"SELECT {Col("session_hash")}, {Col("user_id")}, {Col("created_at")}, {Col("last_seen_at")}, " +
            $"{Col("expires_at")}, {Col("revoked_at")}, {Col("revoke_reason")}, {Col("source_ip")}, " +
            $"{Col("credential_stamp")} FROM {Table(SessionsTable)}";

        public async Task CreateSessionAsync(LocalSession session, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(session);
            var sql =
                $"INSERT INTO {Table(SessionsTable)} ({Col("session_hash")}, {Col("user_id")}, {Col("created_at")}, " +
                $"{Col("last_seen_at")}, {Col("expires_at")}, {Col("revoked_at")}, {Col("revoke_reason")}, " +
                $"{Col("source_ip")}, {Col("credential_stamp")}) VALUES (@hash, @user, @created, @seen, @expires, " +
                "@revoked, @reason, @ip, @stamp)";
            await RunAsync(sql, new Dictionary<string, object?>
            {
                ["hash"] = session.SessionHash,
                ["user"] = session.UserId,
                ["created"] = Stamp(session.CreatedAt),
                ["seen"] = Stamp(session.LastSeenAt),
                ["expires"] = Stamp(session.ExpiresAt),
                ["revoked"] = session.RevokedAt is { } r ? Stamp(r) : null,
                ["reason"] = session.RevokeReason,
                ["ip"] = session.SourceIp,
                ["stamp"] = session.CredentialStamp,
            }, 0, cancellationToken).ConfigureAwait(false);
        }

        public async Task<LocalSession?> GetSessionAsync(string sessionHash, CancellationToken cancellationToken)
        {
            var result = await RunAsync($"{SessionSelect} WHERE {Col("session_hash")} = @hash",
                new Dictionary<string, object?> { ["hash"] = sessionHash }, 1, cancellationToken).ConfigureAwait(false);
            return result.Rows.Count == 0 ? null : MapSession(result.Rows[0]);
        }

        public async Task TouchSessionAsync(string sessionHash, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
        {
            // Only forward, and never on a revoked row: a late touch must not resurrect activity.
            var sql =
                $"UPDATE {Table(SessionsTable)} SET {Col("last_seen_at")} = @seen " +
                $"WHERE {Col("session_hash")} = @hash AND {Col("revoked_at")} IS NULL AND {Col("last_seen_at")} < @seen";
            await RunAsync(sql, new Dictionary<string, object?>
            {
                ["hash"] = sessionHash, ["seen"] = Stamp(lastSeenAt),
            }, 0, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> RevokeSessionAsync(string sessionHash, DateTimeOffset revokedAt, string reason, CancellationToken cancellationToken)
        {
            var sql =
                $"UPDATE {Table(SessionsTable)} SET {Col("revoked_at")} = @at, {Col("revoke_reason")} = @reason " +
                $"WHERE {Col("session_hash")} = @hash AND {Col("revoked_at")} IS NULL";
            var result = await RunAsync(sql, new Dictionary<string, object?>
            {
                ["hash"] = sessionHash, ["at"] = Stamp(revokedAt), ["reason"] = reason,
            }, 0, cancellationToken).ConfigureAwait(false);
            return result.RowsAffected > 0;
        }

        public async Task<int> RevokeUserSessionsAsync(string userId, DateTimeOffset revokedAt, string reason, CancellationToken cancellationToken)
        {
            var sql =
                $"UPDATE {Table(SessionsTable)} SET {Col("revoked_at")} = @at, {Col("revoke_reason")} = @reason " +
                $"WHERE {Col("user_id")} = @user AND {Col("revoked_at")} IS NULL";
            var result = await RunAsync(sql, new Dictionary<string, object?>
            {
                ["user"] = userId, ["at"] = Stamp(revokedAt), ["reason"] = reason,
            }, 0, cancellationToken).ConfigureAwait(false);
            return result.RowsAffected;
        }

        public async Task<int> RebindSessionCredentialStampAsync(
            string userId, string fromStamp, string toStamp, CancellationToken cancellationToken)
        {
            var sql =
                $"UPDATE {Table(SessionsTable)} SET {Col("credential_stamp")} = @to " +
                $"WHERE {Col("user_id")} = @user AND {Col("credential_stamp")} = @from AND {Col("revoked_at")} IS NULL";
            var result = await RunAsync(sql, new Dictionary<string, object?>
            {
                ["user"] = userId, ["from"] = fromStamp, ["to"] = toStamp,
            }, 0, cancellationToken).ConfigureAwait(false);
            return result.RowsAffected;
        }

        public async Task<IReadOnlyList<LocalSession>> ListLiveSessionsAsync(
            string userId, DateTimeOffset now, int maxRows, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
            var sql =
                $"{SessionSelect} WHERE {Col("user_id")} = @user AND {Col("revoked_at")} IS NULL AND {Col("expires_at")} > @now" +
                _dialect.Pagination(new[] { Col("created_at"), Col("session_hash") }, offset: null, limit: maxRows);
            var result = await RunAsync(sql, new Dictionary<string, object?>
            {
                ["user"] = userId, ["now"] = Stamp(now),
            }, maxRows, cancellationToken).ConfigureAwait(false);
            return result.Rows.Select(MapSession).ToList();
        }

        public async Task PurgeSessionsExpiredBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            await RunAsync($"DELETE FROM {Table(SessionsTable)} WHERE {Col("expires_at")} < @cutoff",
                new Dictionary<string, object?> { ["cutoff"] = Stamp(cutoff) }, 0, cancellationToken).ConfigureAwait(false);
        }

        public async Task<LocalLockoutState> GetLockoutAsync(string userId, CancellationToken cancellationToken)
            => (await ReadLockoutRowAsync(userId, cancellationToken).ConfigureAwait(false))?.State ?? LocalLockoutState.None;

        private sealed record LockoutRow(LocalLockoutState State, int Version);

        private async Task<LockoutRow?> ReadLockoutRowAsync(string userId, CancellationToken cancellationToken)
        {
            var result = await RunAsync(
                $"SELECT {Col("failed_count")}, {Col("locked_until")}, {Col("version")} FROM {Table(LockoutsTable)} " +
                $"WHERE {Col("user_id")} = @user",
                new Dictionary<string, object?> { ["user"] = userId }, 1, cancellationToken).ConfigureAwait(false);
            if (result.Rows.Count == 0)
                return null;
            var row = result.Rows[0];
            return new LockoutRow(
                new LocalLockoutState(Convert.ToInt32(row[0], CultureInfo.InvariantCulture), ParseNullable(row[1])),
                Convert.ToInt32(row[2], CultureInfo.InvariantCulture));
        }

        public async Task<LocalFailedLoginResult> RecordFailedLoginAsync(
            string userId, DateTimeOffset at, int threshold, DateTimeOffset lockedUntil, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
            for (var attempt = 0; attempt < MaxOptimisticAttempts; attempt++)
            {
                var current = await ReadLockoutRowAsync(userId, cancellationToken).ConfigureAwait(false);
                var state = current?.State ?? LocalLockoutState.None;
                if (state.IsLockedAt(at))
                    return new LocalFailedLoginResult(state, LockedNow: false);

                var count = state.FailedCount + 1;
                var locks = count >= threshold;
                var next = locks ? new LocalLockoutState(0, lockedUntil) : new LocalLockoutState(count, null);
                var parameters = new Dictionary<string, object?>
                {
                    ["user"] = userId,
                    ["count"] = next.FailedCount,
                    ["until"] = next.LockedUntil is { } u ? Stamp(u) : null,
                    ["at"] = Stamp(at),
                };

                if (current is null)
                {
                    parameters["version"] = 1;
                    try
                    {
                        await RunAsync(
                            $"INSERT INTO {Table(LockoutsTable)} ({Col("user_id")}, {Col("failed_count")}, {Col("locked_until")}, " +
                            $"{Col("last_failure_at")}, {Col("version")}) VALUES (@user, @count, @until, @at, @version)",
                            parameters, 0, cancellationToken).ConfigureAwait(false);
                        return new LocalFailedLoginResult(next, locks);
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        // A concurrent failure inserted the row first (primary-key violation). Re-read
                        // and count on top of it; a failure that is not that race surfaces below.
                        if (await ReadLockoutRowAsync(userId, cancellationToken).ConfigureAwait(false) is null)
                            throw;
                        continue;
                    }
                }

                parameters["version"] = current.Version;
                parameters["nextVersion"] = current.Version + 1;
                var updated = await RunAsync(
                    $"UPDATE {Table(LockoutsTable)} SET {Col("failed_count")} = @count, {Col("locked_until")} = @until, " +
                    $"{Col("last_failure_at")} = @at, {Col("version")} = @nextVersion " +
                    $"WHERE {Col("user_id")} = @user AND {Col("version")} = @version",
                    parameters, 0, cancellationToken).ConfigureAwait(false);
                if (updated.RowsAffected > 0)
                    return new LocalFailedLoginResult(next, locks);
            }

            throw new InvalidOperationException(
                $"The lockout counter could not be updated after {MaxOptimisticAttempts} concurrent attempts.");
        }

        public async Task ClearLockoutAsync(string userId, CancellationToken cancellationToken)
        {
            await RunAsync($"DELETE FROM {Table(LockoutsTable)} WHERE {Col("user_id")} = @user",
                new Dictionary<string, object?> { ["user"] = userId }, 0, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> ClearExpiredLockoutAsync(string userId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            // Stamps are fixed-width UTC round-trip text, so the comparison orders like the instants.
            var deleted = await RunAsync(
                $"DELETE FROM {Table(LockoutsTable)} WHERE {Col("user_id")} = @user " +
                $"AND {Col("locked_until")} IS NOT NULL AND {Col("locked_until")} <= @now",
                new Dictionary<string, object?> { ["user"] = userId, ["now"] = Stamp(now) }, 0, cancellationToken)
                .ConfigureAwait(false);
            return deleted.RowsAffected > 0;
        }

        public async Task<IReadOnlyList<LocalPasswordHistoryEntry>> GetPasswordHistoryAsync(
            string userId, int maxEntries, CancellationToken cancellationToken)
        {
            var rows = await ReadPasswordHistoryAsync(userId, maxEntries, cancellationToken).ConfigureAwait(false);
            return rows.Select(r => r.Entry).ToList();
        }

        /// <summary>The newest <paramref name="maxEntries"/> history rows, newest first by insertion order.</summary>
        private async Task<IReadOnlyList<(LocalPasswordHistoryEntry Entry, long Seq)>> ReadPasswordHistoryAsync(
            string userId, int maxEntries, CancellationToken cancellationToken)
        {
            if (maxEntries < 1)
                return Array.Empty<(LocalPasswordHistoryEntry, long)>();
            var sql =
                $"SELECT {Col("password_hash")}, {Col("changed_at")}, {Col("seq")} FROM {Table(PasswordHistoryTable)} " +
                $"WHERE {Col("user_id")} = @user" +
                _dialect.Pagination(new[] { $"{Col("seq")} DESC", $"{Col("entry_id")} DESC" }, offset: null, limit: maxEntries);
            var result = await RunAsync(sql, new Dictionary<string, object?> { ["user"] = userId }, maxEntries, cancellationToken)
                .ConfigureAwait(false);
            return result.Rows
                .Select(r => (new LocalPasswordHistoryEntry(Convert.ToString(r[0], CultureInfo.InvariantCulture)!, Parse(r[1])),
                    Convert.ToInt64(r[2], CultureInfo.InvariantCulture)))
                .ToList();
        }

        public async Task AppendPasswordHistoryAsync(
            string userId, string passwordHash, DateTimeOffset changedAt, int keep, CancellationToken cancellationToken)
        {
            // Next seq for this user. Two concurrent changes of one user's password can read the same
            // MAX and tie; the entry_id tie-break keeps the order deterministic, and both stay history.
            var newest = await ReadPasswordHistoryAsync(userId, 1, cancellationToken).ConfigureAwait(false);
            var seq = newest.Count == 0 ? 1 : newest[0].Seq + 1;
            await RunAsync(
                $"INSERT INTO {Table(PasswordHistoryTable)} ({Col("entry_id")}, {Col("user_id")}, {Col("changed_at")}, " +
                $"{Col("seq")}, {Col("password_hash")}) VALUES (@id, @user, @at, @seq, @hash)",
                new Dictionary<string, object?>
                {
                    ["id"] = Guid.NewGuid().ToString("N"), ["user"] = userId, ["at"] = Stamp(changedAt), ["seq"] = seq,
                    ["hash"] = passwordHash,
                }, 0, cancellationToken).ConfigureAwait(false);

            // Drop everything older than the newest `keep` entries. Read the cut point first so the
            // DELETE is one portable statement (MySQL refuses a subquery on the table it deletes from).
            var kept = await ReadPasswordHistoryAsync(userId, Math.Max(keep, 1), cancellationToken).ConfigureAwait(false);
            if (kept.Count < Math.Max(keep, 1))
                return;
            await RunAsync(
                $"DELETE FROM {Table(PasswordHistoryTable)} WHERE {Col("user_id")} = @user AND {Col("seq")} < @cut",
                new Dictionary<string, object?> { ["user"] = userId, ["cut"] = kept[^1].Seq },
                0, cancellationToken).ConfigureAwait(false);
        }

        public async Task AppendAuditAsync(AuthAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(auditEvent);
            await RunAsync(
                $"INSERT INTO {Table(AuditTable)} ({Col("event_id")}, {Col("occurred_at")}, {Col("kind")}, {Col("outcome")}, " +
                $"{Col("subject_id")}, {Col("actor_id")}, {Col("source_ip")}, {Col("reason")}) " +
                "VALUES (@id, @at, @kind, @outcome, @subject, @actor, @ip, @reason)",
                new Dictionary<string, object?>
                {
                    ["id"] = auditEvent.EventId.ToString("N"),
                    ["at"] = Stamp(auditEvent.OccurredAt),
                    ["kind"] = auditEvent.Kind.ToString(),
                    ["outcome"] = auditEvent.Outcome.ToString(),
                    ["subject"] = auditEvent.SubjectId,
                    ["actor"] = auditEvent.ActorId,
                    ["ip"] = auditEvent.SourceIp,
                    ["reason"] = auditEvent.Reason,
                }, 0, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<AuthAuditEvent>> ReadAuditAsync(DateTimeOffset since, int maxRows, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
            var sql =
                $"SELECT {Col("event_id")}, {Col("occurred_at")}, {Col("kind")}, {Col("outcome")}, {Col("subject_id")}, " +
                $"{Col("actor_id")}, {Col("source_ip")}, {Col("reason")} FROM {Table(AuditTable)} WHERE {Col("occurred_at")} >= @since" +
                _dialect.Pagination(new[] { Col("occurred_at"), Col("event_id") }, offset: null, limit: maxRows);
            var result = await RunAsync(sql, new Dictionary<string, object?> { ["since"] = Stamp(since) }, maxRows, cancellationToken)
                .ConfigureAwait(false);
            return result.Rows.Select(r => new AuthAuditEvent(
                Guid.ParseExact(Convert.ToString(r[0], CultureInfo.InvariantCulture)!, "N"),
                Parse(r[1]),
                Enum.Parse<AuthAuditKind>(Convert.ToString(r[2], CultureInfo.InvariantCulture)!),
                Enum.Parse<AuthAuditOutcome>(Convert.ToString(r[3], CultureInfo.InvariantCulture)!),
                r[4] as string, r[5] as string, r[6] as string, r[7] as string)).ToList();
        }

        private static LocalSession MapSession(object?[] row) => new(
            Convert.ToString(row[0], CultureInfo.InvariantCulture)!,
            Convert.ToString(row[1], CultureInfo.InvariantCulture)!,
            Parse(row[2]),
            Parse(row[3]),
            Parse(row[4]),
            ParseNullable(row[5]),
            row[6] as string,
            row[7] as string,
            Convert.ToString(row[8], CultureInfo.InvariantCulture)!);

        /// <summary>Fixed-width, string-sortable UTC text: <c>2026-11-17T10:00:00.0000000+00:00</c>.</summary>
        internal static string Stamp(DateTimeOffset value) =>
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        private static DateTimeOffset Parse(object? value) =>
            DateTimeOffset.ParseExact(Convert.ToString(value, CultureInfo.InvariantCulture)!, "O",
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        private static DateTimeOffset? ParseNullable(object? value) => value is null ? null : Parse(value);
    }
}
