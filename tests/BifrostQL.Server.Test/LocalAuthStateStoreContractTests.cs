using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// One behavioural contract for both <see cref="ILocalAuthStateStore"/> implementations: the
    /// production DB store (on SQLite here) and the in-memory test store answer every fact the
    /// same way. Each multi-row fact uses two users, so a store that ignored the user key would fail.
    /// </summary>
    public sealed class LocalAuthStateStoreContractTests : IDisposable
    {
        private static readonly DateTimeOffset T0 = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        private readonly List<SqliteConnection> _keepAlive = new();

        public void Dispose()
        {
            foreach (var c in _keepAlive) c.Dispose();
            SqliteConnection.ClearAllPools();
        }

        public static IEnumerable<object[]> Backends => new[] { new object[] { "db" }, new object[] { "memory" } };

        private ILocalAuthStateStore Make(string backend)
        {
            if (backend == "memory")
                return new InMemoryLocalAuthStateStore();
            var conn = $"Data Source=bifrost_auth_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var keepAlive = new SqliteConnection(conn);
            keepAlive.Open();
            _keepAlive.Add(keepAlive);
            return new DbLocalAuthStateStore(new SqliteDbConnFactory(conn));
        }

        private static LocalSession Session(string hash, string user, DateTimeOffset created, TimeSpan? life = null) =>
            new(hash, user, created, created, created + (life ?? TimeSpan.FromHours(8)), null, null, "127.0.0.1", "stamp-1");

        private static readonly CancellationToken None = CancellationToken.None;

        [Fact]
        public async Task TheStateTables_AreNeverPartOfAServedModel_EvenWithoutMetadata()
        {
            // The DB store provisions its tables in the app-user database, which is usually the
            // database a GraphQL endpoint serves. Hidden by name, they cannot be read through it.
            await using var harness = await S3.S3ListingRealDbHarness.StartAsync(
                nameof(TheStateTables_AreNeverPartOfAServedModel_EvenWithoutMetadata),
                Array.Empty<string>(),
                new[]
                {
                    $"CREATE TABLE {DbLocalAuthStateStore.SessionsTable} (session_hash TEXT PRIMARY KEY, user_id TEXT)",
                    $"CREATE TABLE {DbLocalAuthStateStore.AuditTable} (event_id TEXT PRIMARY KEY, kind TEXT)",
                    "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT)",
                });

            var model = await harness.Reads.GetModelAsync();

            model.Tables.Select(t => t.DbName).Should().Contain("widgets", "control: an ordinary table is served")
                .And.NotContain(new[] { DbLocalAuthStateStore.SessionsTable, DbLocalAuthStateStore.AuditTable });
            model.HiddenTables.Select(t => t.DbName).Should()
                .Contain(new[] { DbLocalAuthStateStore.SessionsTable, DbLocalAuthStateStore.AuditTable });
        }

        [Theory, MemberData(nameof(Backends))]
        public async Task Session_RoundTrips_AndAnUnknownHashIsNull(string backend)
        {
            var store = Make(backend);
            var session = Session("hash-a", "alice", T0);

            await store.CreateSessionAsync(session, None);

            (await store.GetSessionAsync("hash-a", None)).Should().Be(session);
            (await store.GetSessionAsync("hash-missing", None)).Should().BeNull();
        }

        [Theory, MemberData(nameof(Backends))]
        public async Task Touch_MovesOnlyForward_AndNeverOnARevokedSession(string backend)
        {
            var store = Make(backend);
            await store.CreateSessionAsync(Session("hash-a", "alice", T0), None);
            await store.CreateSessionAsync(Session("hash-b", "alice", T0), None);

            await store.TouchSessionAsync("hash-a", T0.AddMinutes(5), None);
            await store.TouchSessionAsync("hash-a", T0.AddMinutes(2), None);
            await store.RevokeSessionAsync("hash-b", T0.AddMinutes(1), LocalSessionRevokeReasons.Logout, None);
            await store.TouchSessionAsync("hash-b", T0.AddMinutes(5), None);

            (await store.GetSessionAsync("hash-a", None))!.LastSeenAt.Should().Be(T0.AddMinutes(5));
            (await store.GetSessionAsync("hash-b", None))!.LastSeenAt.Should().Be(T0);
        }

        [Theory, MemberData(nameof(Backends))]
        public async Task Revoke_IsOneShot_AndRecordsItsReason(string backend)
        {
            var store = Make(backend);
            await store.CreateSessionAsync(Session("hash-a", "alice", T0), None);

            (await store.RevokeSessionAsync("hash-a", T0.AddMinutes(1), LocalSessionRevokeReasons.Logout, None)).Should().BeTrue();
            (await store.RevokeSessionAsync("hash-a", T0.AddMinutes(2), LocalSessionRevokeReasons.Administrator, None)).Should().BeFalse();

            var revoked = await store.GetSessionAsync("hash-a", None);
            revoked!.RevokedAt.Should().Be(T0.AddMinutes(1));
            revoked.RevokeReason.Should().Be(LocalSessionRevokeReasons.Logout);
        }

        [Theory, MemberData(nameof(Backends))]
        public async Task RevokeUserSessions_EndsEveryLiveSessionOfThatUserOnly(string backend)
        {
            var store = Make(backend);
            await store.CreateSessionAsync(Session("a1", "alice", T0), None);
            await store.CreateSessionAsync(Session("a2", "alice", T0.AddMinutes(1)), None);
            await store.CreateSessionAsync(Session("b1", "bob", T0), None);

            (await store.RevokeUserSessionsAsync("alice", T0.AddMinutes(2), LocalSessionRevokeReasons.PasswordChanged, None))
                .Should().Be(2);

            (await store.ListLiveSessionsAsync("alice", T0.AddMinutes(3), 10, None)).Should().BeEmpty();
            (await store.ListLiveSessionsAsync("bob", T0.AddMinutes(3), 10, None)).Select(s => s.SessionHash)
                .Should().Equal("b1");
        }

        [Theory, MemberData(nameof(Backends))]
        public async Task ListLive_IsOldestFirst_SkipsExpiredAndRevoked_AndHonoursTheRowCap(string backend)
        {
            var store = Make(backend);
            await store.CreateSessionAsync(Session("s3", "alice", T0.AddMinutes(3)), None);
            await store.CreateSessionAsync(Session("s1", "alice", T0.AddMinutes(1)), None);
            await store.CreateSessionAsync(Session("s2", "alice", T0.AddMinutes(2)), None);
            await store.CreateSessionAsync(Session("old", "alice", T0, life: TimeSpan.FromMinutes(5)), None);
            await store.CreateSessionAsync(Session("gone", "alice", T0.AddMinutes(4)), None);
            await store.RevokeSessionAsync("gone", T0.AddMinutes(5), LocalSessionRevokeReasons.Logout, None);

            var now = T0.AddMinutes(10);
            (await store.ListLiveSessionsAsync("alice", now, 10, None)).Select(s => s.SessionHash)
                .Should().Equal("s1", "s2", "s3");
            (await store.ListLiveSessionsAsync("alice", now, 2, None)).Select(s => s.SessionHash)
                .Should().Equal("s1", "s2");
        }

        [Theory, MemberData(nameof(Backends))]
        public async Task Purge_RemovesOnlySessionsExpiredBeforeTheCutoff(string backend)
        {
            var store = Make(backend);
            await store.CreateSessionAsync(Session("short", "alice", T0, life: TimeSpan.FromMinutes(5)), None);
            await store.CreateSessionAsync(Session("long", "alice", T0), None);

            await store.PurgeSessionsExpiredBeforeAsync(T0.AddMinutes(6), None);

            (await store.GetSessionAsync("short", None)).Should().BeNull();
            (await store.GetSessionAsync("long", None)).Should().NotBeNull();
        }

        [Theory, MemberData(nameof(Backends))]
        public async Task FailedLogins_CountPerUser_LockAtTheThreshold_AndResetTheCounter(string backend)
        {
            var store = Make(backend);
            var until = T0.AddMinutes(30);

            (await store.RecordFailedLoginAsync("alice", T0, 3, until, None)).Should().Be(
                new LocalFailedLoginResult(new LocalLockoutState(1, null), false));
            await store.RecordFailedLoginAsync("bob", T0, 3, until, None);
            await store.RecordFailedLoginAsync("alice", T0, 3, until, None);
            var third = await store.RecordFailedLoginAsync("alice", T0, 3, until, None);

            third.LockedNow.Should().BeTrue();
            third.State.Should().Be(new LocalLockoutState(0, until));
            (await store.GetLockoutAsync("alice", None)).IsLockedAt(T0.AddMinutes(29)).Should().BeTrue();
            (await store.GetLockoutAsync("alice", None)).IsLockedAt(T0.AddMinutes(30)).Should().BeFalse();
            (await store.GetLockoutAsync("bob", None)).Should().Be(new LocalLockoutState(1, null));
        }

        [Theory, MemberData(nameof(Backends))]
        public async Task AFailureWhileLocked_NeitherCountsNorExtendsTheLock(string backend)
        {
            var store = Make(backend);
            var until = T0.AddMinutes(30);
            await store.RecordFailedLoginAsync("alice", T0, 1, until, None);

            var during = await store.RecordFailedLoginAsync("alice", T0.AddMinutes(1), 1, T0.AddMinutes(31), None);

            during.LockedNow.Should().BeFalse();
            (await store.GetLockoutAsync("alice", None)).Should().Be(new LocalLockoutState(0, until));
        }

        [Theory, MemberData(nameof(Backends))]
        public async Task ConcurrentFailures_AreEachCounted(string backend)
        {
            var store = Make(backend);

            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                store.RecordFailedLoginAsync("alice", T0, 100, T0.AddMinutes(30), None)));

            (await store.GetLockoutAsync("alice", None)).FailedCount.Should().Be(8,
                "a lost update would let an attacker spend more guesses than the threshold allows");
        }

        [Theory, MemberData(nameof(Backends))]
        public async Task ClearLockout_EndsTheLockAndTheCount(string backend)
        {
            var store = Make(backend);
            await store.RecordFailedLoginAsync("alice", T0, 1, DateTimeOffset.MaxValue, None);

            await store.ClearLockoutAsync("alice", None);

            (await store.GetLockoutAsync("alice", None)).Should().Be(LocalLockoutState.None);
        }

        [Theory, MemberData(nameof(Backends))]
        public async Task AnIndefiniteLock_RoundTrips(string backend)
        {
            var store = Make(backend);

            await store.RecordFailedLoginAsync("alice", T0, 1, DateTimeOffset.MaxValue, None);

            (await store.GetLockoutAsync("alice", None)).IsLockedAt(T0.AddYears(50)).Should().BeTrue();
        }

        [Theory, MemberData(nameof(Backends))]
        public async Task PasswordHistory_IsNewestFirst_KeepsOnlyTheNewestN_PerUser(string backend)
        {
            var store = Make(backend);
            for (var i = 1; i <= 5; i++)
                await store.AppendPasswordHistoryAsync("alice", $"hash-{i}", T0.AddDays(i), keep: 3, None);
            await store.AppendPasswordHistoryAsync("bob", "bob-hash", T0, keep: 3, None);

            (await store.GetPasswordHistoryAsync("alice", 10, None)).Select(e => e.PasswordHash)
                .Should().Equal("hash-5", "hash-4", "hash-3");
            (await store.GetPasswordHistoryAsync("alice", 2, None)).Select(e => e.PasswordHash)
                .Should().Equal("hash-5", "hash-4");
            (await store.GetPasswordHistoryAsync("bob", 10, None)).Select(e => e.PasswordHash)
                .Should().Equal("bob-hash");
        }

        [Theory, MemberData(nameof(Backends))]
        public async Task Audit_RoundTripsInTimeOrder_FromTheGivenInstant(string backend)
        {
            var store = Make(backend);
            var late = new AuthAuditEvent(Guid.NewGuid(), T0.AddMinutes(2), AuthAuditKind.Logout, AuthAuditOutcome.Success,
                "alice", null, "10.0.0.1", null);
            var early = new AuthAuditEvent(Guid.NewGuid(), T0.AddMinutes(1), AuthAuditKind.LoginFailed, AuthAuditOutcome.Failure,
                null, null, "10.0.0.2", AuthAuditReasons.UnknownAccount);
            var before = new AuthAuditEvent(Guid.NewGuid(), T0, AuthAuditKind.AccountUnlocked, AuthAuditOutcome.Success,
                "alice", "admin-1", null, AuthAuditReasons.Administrator);
            await store.AppendAuditAsync(late, None);
            await store.AppendAuditAsync(before, None);
            await store.AppendAuditAsync(early, None);

            (await store.ReadAuditAsync(T0.AddMinutes(1), 10, None)).Should().Equal(early, late);
            (await store.ReadAuditAsync(T0, 1, None)).Should().Equal(before);
        }
    }
}
