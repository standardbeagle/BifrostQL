using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Drives the local-auth login, session and cookie revalidation through the production
    /// registrar (<c>AddBifrostLocalAuth</c> + <c>UseBifrostLocalAuth</c>) on a TestServer over a
    /// file-backed SQLite app-user table.
    /// </summary>
    public sealed class LocalAuthLoginHostTests : IAsyncLifetime
    {
        private const string Login = "alice@club.test";
        private const string Password = "correct horse battery";
        private const string TestIpHeader = "X-Test-Remote-Ip";

        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-localauth-host-{Guid.NewGuid():N}.db");
        private string ConnectionString => $"Data Source={_dbPath}";
        private WebApplication? _app;
        private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

        /// <summary>
        /// A 1,000-iteration hasher for fixtures. These facts exercise sessions, lockout and rate
        /// limits, not the work factor; at the production 210,000 every login costs one full PBKDF2
        /// run, which put most facts here at 3-50 s each. The V3 hash embeds its own iteration
        /// count, so seeds and the store agree and no rehash runs. The production work factor keeps
        /// its own facts below, on stores built without this hasher.
        /// </summary>
        private static readonly PasswordHasher<string> CheapHasher = new(
            Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));

        public Task InitializeAsync()
        {
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE app_users (id TEXT PRIMARY KEY, email TEXT, password_hash TEXT, " +
                "display_name TEXT, tenant_id TEXT, roles TEXT);" +
                "INSERT INTO app_users VALUES ('user-1', @email, @hash, 'Alice', 'club-7', 'admin,editor');";
            cmd.Parameters.AddWithValue("@email", Login);
            cmd.Parameters.AddWithValue("@hash", CheapHasher.HashPassword(Login, Password));
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            if (_app is not null)
                await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private async Task<HttpClient> StartAsync(
            Action<LocalAuthOptions> configure, Action<IServiceCollection>? services = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<TimeProvider>(_clock);
            builder.Services.AddBifrostLocalAuth(ConnectionString, configure);
            // The production registrar, with only the hasher swapped through the store's constructor
            // seam. A fact's own services run after, so it can still replace the store.
            builder.Services.AddSingleton(sp => new LocalUserStore(
                sp.GetRequiredService<IDbConnFactory>(), sp.GetRequiredService<LocalAuthOptions>(), CheapHasher));
            services?.Invoke(builder.Services);
            _app = builder.Build();
            // TestServer has no remote address; let each request name its source IP.
            _app.Use((context, next) =>
            {
                if (context.Request.Headers.TryGetValue(TestIpHeader, out var ip))
                    context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                return next();
            });
            _app.UseAuthentication();
            _app.UseBifrostLocalAuth();
            await _app.StartAsync();
            return _app.GetTestClient();
        }

        private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string password, string ip)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
            {
                Content = JsonContent.Create(new { login = Login, password }),
            };
            request.Headers.Add(TestIpHeader, ip);
            return await client.SendAsync(request);
        }

        private static string SessionCookie(HttpResponseMessage login)
        {
            login.StatusCode.Should().Be(HttpStatusCode.NoContent);
            return login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        }

        private static async Task<HttpResponseMessage> GetSessionAsync(HttpClient client, string cookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/session");
            request.Headers.Add("Cookie", cookie);
            return await client.SendAsync(request);
        }

        private void Execute(string sql)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        [Fact]
        public async Task GuessesSpreadOverManyIps_AreCappedPerAccount()
        {
            var client = await StartAsync(o =>
            {
                o.MaxLoginAttemptsPerSource = 100;
                o.MaxLoginAttemptsPerAccount = 3;
            });

            for (var i = 1; i <= 3; i++)
                (await PostLoginAsync(client, "wrong", $"10.0.0.{i}")).StatusCode
                    .Should().Be(HttpStatusCode.Unauthorized, "attempts under the account cap are verified");

            // A fresh source IP, so only the per-account axis can refuse it. Even the right
            // password is not verified once the account window is full.
            (await PostLoginAsync(client, Password, "10.0.0.99")).StatusCode
                .Should().Be(HttpStatusCode.TooManyRequests);
        }

        [Fact]
        public async Task StateTables_AreProvisionedAtStartup_BeforeAnyRequest()
        {
            await StartAsync(_ => { });

            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '\\_bifrost\\_auth\\_%' ESCAPE '\\' ORDER BY name";
            var names = new List<string>();
            await using (var reader = await cmd.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    names.Add(reader.GetString(0));

            names.Should().Equal(new[]
            {
                DbLocalAuthStateStore.AuditTable, DbLocalAuthStateStore.LockoutsTable,
                DbLocalAuthStateStore.PasswordHistoryTable, DbLocalAuthStateStore.SessionsTable,
            }, "the host creates the tables on start, so no request races their creation and missing DDL rights fail the start");
        }

        [Fact]
        public async Task ParallelGuesses_FromOneSource_AreAdmittedOnlyUpToTheCap()
        {
            const int cap = 4;
            const int attempts = 20;
            var gate = new GatedHasher(attempts);
            var client = await StartAsync(
                o =>
                {
                    o.MaxLoginAttemptsPerSource = cap;
                    o.MaxLoginAttemptsPerAccount = 1000;
                },
                services => services.AddSingleton(sp => new LocalUserStore(
                    sp.GetRequiredService<IDbConnFactory>(), sp.GetRequiredService<LocalAuthOptions>(), gate)));

            // Every attempt is in flight at once: the hasher holds each verification open until
            // all 20 requests have either reached it or been refused, so no failure can be
            // recorded before every request has passed (or failed) admission. A limiter that
            // checks first and counts only after a failed verify lets all 20 into the hasher.
            var sends = Enumerable.Range(0, attempts).Select(async _ =>
            {
                var response = await PostLoginAsync(client, "wrong", "10.0.0.1");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    gate.Settle();
                return response;
            }).ToList();
            await gate.AllSettled.WaitAsync(TimeSpan.FromSeconds(30));
            gate.Release();
            var responses = await Task.WhenAll(sends);

            gate.Entered.Should().Be(cap,
                "each attempt is reserved before the hash runs, so only the cap's worth reach the verifier");
            responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).Should().Be(cap);
            responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests).Should().Be(attempts - cap);
        }

        /// <summary>
        /// A hasher whose verification blocks until <see cref="Release"/>, counting the callers that
        /// reached it. <see cref="AllSettled"/> completes once every expected request has either
        /// entered the verifier or been refused before it (<see cref="Settle"/>).
        /// </summary>
        private sealed class GatedHasher : IPasswordHasher<string>
        {
            private readonly PasswordHasher<string> _inner = new(
                Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));
            private readonly ManualResetEventSlim _release = new(false);
            private readonly TaskCompletionSource _allSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly int _expected;
            private int _entered;
            private int _settled;

            public GatedHasher(int expected) => _expected = expected;

            public int Entered => Volatile.Read(ref _entered);
            public Task AllSettled => _allSettled.Task;
            public void Release() => _release.Set();

            public void Settle()
            {
                if (Interlocked.Increment(ref _settled) == _expected)
                    _allSettled.TrySetResult();
            }

            public string HashPassword(string user, string password) => _inner.HashPassword(user, password);

            public PasswordVerificationResult VerifyHashedPassword(string user, string hashedPassword, string providedPassword)
            {
                Interlocked.Increment(ref _entered);
                Settle();
                _release.Wait(TimeSpan.FromSeconds(30));
                return PasswordVerificationResult.Failed;
            }
        }

        [Fact]
        public async Task RoleChange_RevokesEveryIssuedSession()
        {
            // A role or tenant change used to rewrite the live cookie's claims in place. It now
            // ends every session of that user, so no session ever carries privileges it was not
            // signed in with.
            var client = await StartAsync(o => o.SessionRevalidationInterval = TimeSpan.Zero);
            var first = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));
            var second = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.2"));
            (await GetSessionAsync(client, second)).StatusCode.Should().Be(HttpStatusCode.OK, "control");

            Execute("UPDATE app_users SET roles = 'member', tenant_id = 'club-9' WHERE id = 'user-1'");

            (await GetSessionAsync(client, first)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await GetSessionAsync(client, second)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "the change revokes every session of the user, not only the one that noticed it");
            var audit = await AuditAsync();
            audit.Should().Contain(e => e.Kind == AuthAuditKind.PrivilegeChanged && e.SubjectId == "user-1");
            audit.Should().Contain(e => e.Kind == AuthAuditKind.SessionRevoked
                && e.Reason == LocalSessionRevokeReasons.PrivilegeChanged);
        }

        [Fact]
        public async Task DisplayNameChange_RefreshesTheSession_WithoutRevokingIt()
        {
            var client = await StartAsync(o => o.SessionRevalidationInterval = TimeSpan.Zero);
            var cookie = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));

            Execute("UPDATE app_users SET display_name = 'Alice B' WHERE id = 'user-1'");

            using var session = await GetSessionAsync(client, cookie);
            session.StatusCode.Should().Be(HttpStatusCode.OK, "a display name does not scope what the session may do");
            using var doc = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("displayName").GetString().Should().Be("Alice B");
        }

        private async Task<IReadOnlyList<AuthAuditEvent>> AuditAsync() =>
            await _app!.Services.GetRequiredService<ILocalAuthStateStore>()
                .ReadAuditAsync(DateTimeOffset.MinValue, 1_000, CancellationToken.None);

        private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string cookie, object? body = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add("Cookie", cookie);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            return await client.SendAsync(request);
        }

        [Fact]
        public async Task Logout_RevokesTheServerSession_SoACopiedCookieStopsWorking()
        {
            var client = await StartAsync(_ => { });
            var cookie = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));
            (await GetSessionAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.OK, "control");

            (await PostAsync(client, "/auth/logout", cookie)).StatusCode.Should().Be(HttpStatusCode.NoContent);

            (await GetSessionAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "logout used to delete only the browser's cookie; a copy of it stayed valid for its whole lifetime");
            (await AuditAsync()).Should().Contain(e => e.Kind == AuthAuditKind.Logout && e.SubjectId == "user-1");
        }

        [Fact]
        public async Task IdleSession_IsRevokedAfterFifteenMinutesByDefault_ActivityKeepsItAlive()
        {
            new LocalAuthOptions().SessionIdleTimeout.Should().Be(TimeSpan.FromMinutes(15), "PCI DSS 8.2.8");
            var client = await StartAsync(_ => { });
            var cookie = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));

            _clock.Advance(TimeSpan.FromMinutes(14));
            (await GetSessionAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.OK, "14 idle minutes");
            _clock.Advance(TimeSpan.FromMinutes(14));
            (await GetSessionAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.OK,
                "control: activity at minute 14 restarted the idle clock");
            _clock.Advance(TimeSpan.FromMinutes(16));
            (await GetSessionAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "16 idle minutes");
            (await AuditAsync()).Should().Contain(e => e.Kind == AuthAuditKind.SessionRevoked
                && e.Reason == LocalSessionRevokeReasons.IdleTimeout);
        }

        [Fact]
        public async Task PasswordChangedInTheRow_RevokesIssuedSessions()
        {
            var client = await StartAsync(o => o.SessionRevalidationInterval = TimeSpan.Zero);
            var cookie = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));

            var newHash = CheapHasher.HashPassword(Login, "a different passphrase");
            Execute($"UPDATE app_users SET password_hash = '{newHash}' WHERE id = 'user-1'");

            (await GetSessionAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await AuditAsync()).Should().Contain(e => e.Reason == LocalSessionRevokeReasons.PasswordChanged);
        }

        [Fact]
        public async Task DisabledAccount_CannotSignIn_AndLosesItsSessions()
        {
            Execute("ALTER TABLE app_users ADD COLUMN disabled INTEGER");
            var client = await StartAsync(o =>
            {
                o.DisabledColumn = "disabled";
                o.SessionRevalidationInterval = TimeSpan.Zero;
            });
            var cookie = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));

            Execute("UPDATE app_users SET disabled = 1 WHERE id = 'user-1'");

            (await GetSessionAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await PostLoginAsync(client, Password, "10.0.0.1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var audit = await AuditAsync();
            audit.Should().Contain(e => e.Reason == LocalSessionRevokeReasons.AccountDisabled);
            audit.Should().Contain(e => e.Kind == AuthAuditKind.LoginFailed && e.Reason == AuthAuditReasons.AccountDisabled);
        }

        [Fact]
        public async Task ConcurrentSessionCap_RevokesTheOldestSession()
        {
            var client = await StartAsync(o => o.MaxConcurrentSessions = 2);
            var first = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));
            _clock.Advance(TimeSpan.FromSeconds(1));
            var second = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));
            _clock.Advance(TimeSpan.FromSeconds(1));
            var third = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));

            (await GetSessionAsync(client, first)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await GetSessionAsync(client, second)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await GetSessionAsync(client, third)).StatusCode.Should().Be(HttpStatusCode.OK);
            new LocalAuthOptions().MaxConcurrentSessions.Should().BeNull("no cap unless the deployment sets one");
        }

        [Fact]
        public async Task AdminEndpoint_RevokesAnotherUsersSessions_OnlyForTheAdminRole()
        {
            Execute("INSERT INTO app_users VALUES ('user-2', 'bob@club.test', " +
                    $"'{CheapHasher.HashPassword("bob@club.test", Password)}', 'Bob', 'club-7', 'member')");
            var client = await StartAsync(o => o.AdminRole = "admin");
            var admin = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));
            using var bobLogin = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
            {
                Content = JsonContent.Create(new { login = "bob@club.test", password = Password }),
            };
            var bob = SessionCookie(await client.SendAsync(bobLogin));

            (await PostAsync(client, "/auth/admin/revoke-sessions", bob, new { userId = "user-1" })).StatusCode
                .Should().Be(HttpStatusCode.Forbidden, "bob does not hold the admin role");
            using (var form = new HttpRequestMessage(HttpMethod.Post, "/auth/admin/revoke-sessions"))
            {
                form.Headers.Add("Cookie", admin);
                form.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["userId"] = "user-2" });
                (await client.SendAsync(form)).StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType,
                    "a cross-site form post cannot reach the endpoint");
            }
            (await GetSessionAsync(client, bob)).StatusCode.Should().Be(HttpStatusCode.OK, "control");

            (await PostAsync(client, "/auth/admin/revoke-sessions", admin, new { userId = "user-2" })).StatusCode
                .Should().Be(HttpStatusCode.NoContent);

            (await GetSessionAsync(client, bob)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await GetSessionAsync(client, admin)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await AuditAsync()).Should().Contain(e => e.Kind == AuthAuditKind.SessionRevoked
                && e.SubjectId == "user-2" && e.ActorId == "user-1" && e.Reason == LocalSessionRevokeReasons.Administrator);
        }

        private Task<HttpClient> StartForLockoutAsync(
            Action<LocalAuthOptions>? configure = null, Action<IServiceCollection>? services = null) =>
            StartAsync(o =>
            {
                // Lift the rate windows so only the lockout counter can refuse.
                o.MaxLoginAttemptsPerSource = 1_000;
                o.MaxLoginAttemptsPerAccount = 1_000;
                o.AdminRole = "admin";
                configure?.Invoke(o);
            }, services);

        private static async Task FailLoginsAsync(HttpClient client, int count)
        {
            for (var i = 0; i < count; i++)
                (await PostLoginAsync(client, "wrong", $"10.1.0.{i % 200 + 1}")).StatusCode
                    .Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task TenConsecutiveFailures_LockTheAccount_ForThirtyMinutes_EvenAgainstTheRightPassword()
        {
            var defaults = new LocalAuthOptions();
            defaults.LockoutThreshold.Should().Be(10, "PCI DSS 8.3.4");
            defaults.LockoutDuration.Should().Be(TimeSpan.FromMinutes(30), "PCI DSS 8.3.4");
            var client = await StartForLockoutAsync();

            await FailLoginsAsync(client, 9);
            SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));
            await FailLoginsAsync(client, 10);

            (await PostLoginAsync(client, Password, "10.0.0.1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "ten consecutive failures since the last success lock the account");
            _clock.Advance(TimeSpan.FromMinutes(29));
            (await PostLoginAsync(client, Password, "10.0.0.1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            _clock.Advance(TimeSpan.FromMinutes(2));
            SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));

            var audit = await AuditAsync();
            audit.Count(e => e.Kind == AuthAuditKind.AccountLocked).Should().Be(1,
                "nine failures, a success, then ten failures: only the second run reaches the threshold");
            audit.Should().Contain(e => e.Kind == AuthAuditKind.LoginFailed && e.Reason == AuthAuditReasons.AccountLocked);
            audit.Should().Contain(e => e.Kind == AuthAuditKind.AccountUnlocked && e.Reason == AuthAuditReasons.LockoutExpired);
        }

        [Fact]
        public async Task RightPassword_DoesNotUndoALockTakenAfterTheLockoutCheck()
        {
            // A concurrent wrong guess locks the account between this login's lockout read and its
            // success path. The success must not wipe that lock.
            var racing = new LockoutRacingStateStore();
            var client = await StartForLockoutAsync(o => o.LockoutThreshold = 1,
                s => s.AddSingleton<ILocalAuthStateStore>(racing));
            racing.AfterFirstLockoutRead = (inner, userId) =>
                inner.RecordFailedLoginAsync(userId, _clock.GetUtcNow(), 1, _clock.GetUtcNow().AddMinutes(30), CancellationToken.None);

            var login = await PostLoginAsync(client, Password, "10.0.0.1");

            login.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "the account was locked before this success could clear its failures");
            (await racing.GetLockoutAsync("user-1", CancellationToken.None)).IsLockedAt(_clock.GetUtcNow())
                .Should().BeTrue("a success must not undo a lock taken a moment before it");
            (await racing.ReadAuditAsync(DateTimeOffset.MinValue, 100, CancellationToken.None)).Should()
                .Contain(e => e.Kind == AuthAuditKind.LoginFailed && e.Reason == AuthAuditReasons.AccountLocked);
        }

        [Fact]
        public async Task LapsedLock_ClearedOnce_KeepsAFailureCountedInBetween_AndAuditsOneUnlock()
        {
            // Two requests read the same lapsed lock. The other one clears it and counts its own wrong
            // guess first; this request's clear must not wipe that count or audit a second unlock.
            var racing = new LockoutRacingStateStore();
            var client = await StartForLockoutAsync(o => o.LockoutThreshold = 5,
                s => s.AddSingleton<ILocalAuthStateStore>(racing));
            var now = _clock.GetUtcNow();
            await racing.RecordFailedLoginAsync("user-1", now.AddHours(-2), 1, now.AddHours(-1), CancellationToken.None);
            racing.AfterFirstLockoutRead = async (inner, userId) =>
            {
                await inner.ClearLockoutAsync(userId, CancellationToken.None);
                await inner.RecordFailedLoginAsync(userId, now, 5, now.AddMinutes(30), CancellationToken.None);
            };

            (await PostLoginAsync(client, "wrong", "10.0.0.1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            (await racing.GetLockoutAsync("user-1", CancellationToken.None)).FailedCount.Should().Be(2,
                "the failure the other request counted after the lapse must survive this request's clear");
            (await racing.ReadAuditAsync(DateTimeOffset.MinValue, 100, CancellationToken.None))
                .Count(e => e.Kind == AuthAuditKind.AccountUnlocked).Should().Be(0,
                    "this request deleted no lock, so it records no unlock (the racing request owns that event)");
        }

        [Fact]
        public async Task IndefiniteLockout_EndsOnlyWhenAnAdministratorUnlocks()
        {
            Execute("INSERT INTO app_users VALUES ('admin-1', 'root@club.test', " +
                    $"'{CheapHasher.HashPassword("root@club.test", Password)}', 'Root', 'club-7', 'admin')");
            var client = await StartForLockoutAsync(o =>
            {
                o.LockoutThreshold = 3;
                o.LockoutDuration = null;
            });
            await FailLoginsAsync(client, 3);
            _clock.Advance(TimeSpan.FromDays(30));
            (await PostLoginAsync(client, Password, "10.0.0.1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "no duration means the lock does not lapse");
            using var adminLogin = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
            {
                Content = JsonContent.Create(new { login = "root@club.test", password = Password }),
            };
            var admin = SessionCookie(await client.SendAsync(adminLogin));

            (await PostAsync(client, "/auth/admin/unlock", admin, new { userId = "user-1" })).StatusCode
                .Should().Be(HttpStatusCode.NoContent);

            SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));
            (await AuditAsync()).Should().Contain(e => e.Kind == AuthAuditKind.AccountUnlocked
                && e.SubjectId == "user-1" && e.ActorId == "admin-1" && e.Reason == AuthAuditReasons.Administrator);
        }

        [Fact]
        public async Task UnknownLogins_LockNothing_AndAnswerLikeALockedAccount()
        {
            var client = await StartForLockoutAsync(o => o.LockoutThreshold = 2);
            await FailLoginsAsync(client, 2);
            using var unknown = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
            {
                Content = JsonContent.Create(new { login = "nobody@club.test", password = "x" }),
            };
            using var unknownResponse = await client.SendAsync(unknown);
            using var locked = await PostLoginAsync(client, Password, "10.0.0.1");

            unknownResponse.StatusCode.Should().Be(locked.StatusCode);
            (await unknownResponse.Content.ReadAsStringAsync()).Should().Be(await locked.Content.ReadAsStringAsync(),
                "a locked account must not be told apart from a missing one");
        }

        private static async Task<HttpResponseMessage> ChangePasswordAsync(HttpClient client, string current, string next)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/password")
            {
                Content = JsonContent.Create(new { login = Login, currentPassword = current, newPassword = next }),
            };
            return await client.SendAsync(request);
        }

        private static async Task<string?> RuleOf(HttpResponseMessage response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("rule").GetString();
        }

        [Fact]
        public async Task PasswordChange_EnforcesLengthAndTheLastFourPasswords()
        {
            var defaults = new LocalAuthOptions();
            defaults.MinPasswordLength.Should().Be(12, "PCI DSS 8.3.6");
            defaults.PasswordRequiresLetterAndDigit.Should().BeTrue("PCI DSS 8.3.6");
            defaults.PasswordHistoryCount.Should().Be(4, "PCI DSS 8.3.7");
            defaults.MaxPasswordAge.Should().BeNull("rotation is the deployment's choice");
            var client = await StartForLockoutAsync();

            (await RuleOf(await ChangePasswordAsync(client, Password, "short pass1"))).Should().Be("min_length");
            (await RuleOf(await ChangePasswordAsync(client, Password, "no digits in this one"))).Should().Be("letter_and_digit");
            (await RuleOf(await ChangePasswordAsync(client, Password, "1234567890123"))).Should().Be("letter_and_digit");

            var used = new[] { "new passphrase no 1", "new passphrase no 2", "new passphrase no 3",
                "new passphrase no 4", "new passphrase no 5" };
            var current = Password;
            foreach (var next in used)
            {
                (await ChangePasswordAsync(client, current, next)).StatusCode.Should().Be(HttpStatusCode.NoContent);
                current = next;
            }

            // used[4] is current; used[0..3] are the four passwords before it (PCI DSS 8.3.7: the
            // current one plus the previous four).
            foreach (var recent in used.Take(4))
                (await RuleOf(await ChangePasswordAsync(client, current, recent))).Should().Be("reused",
                    $"'{recent}' is one of the four passwords before the current one");
            (await ChangePasswordAsync(client, current, "new passphrase no 6")).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await ChangePasswordAsync(client, "new passphrase no 6", used[0])).StatusCode.Should().Be(HttpStatusCode.NoContent,
                "control: one more change puts the first new password five back, outside the window");

            var audit = await AuditAsync();
            audit.Count(e => e.Kind == AuthAuditKind.PasswordChanged).Should().Be(7);
            audit.Should().Contain(e => e.Kind == AuthAuditKind.PasswordChangeRejected && e.Reason == AuthAuditReasons.PasswordPolicy);
        }

        [Fact]
        public async Task PasswordHistoryCountOne_RefusesThePasswordBeforeTheCurrentOne()
        {
            var client = await StartForLockoutAsync(o => o.PasswordHistoryCount = 1);
            var used = new[] { "new passphrase no 1", "new passphrase no 2", "new passphrase no 3",
                "new passphrase no 4", "new passphrase no 5" };
            var current = Password;
            foreach (var next in used)
            {
                (await ChangePasswordAsync(client, current, next)).StatusCode.Should().Be(HttpStatusCode.NoContent);
                current = next;
            }

            (await RuleOf(await ChangePasswordAsync(client, current, used[3]))).Should().Be("reused",
                "a history of one covers the password before the current one");
            (await ChangePasswordAsync(client, current, used[2])).StatusCode.Should().Be(HttpStatusCode.NoContent,
                "control: two changes back is outside a history of one");
        }

        [Fact]
        public async Task PasswordChange_RevokesEverySession_AndTheOldPasswordStopsWorking()
        {
            var client = await StartForLockoutAsync();
            var cookie = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));

            (await ChangePasswordAsync(client, Password, "a brand new passphrase 9")).StatusCode.Should().Be(HttpStatusCode.NoContent);

            (await GetSessionAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "the change revokes sessions at once, not at the next revalidation");
            (await PostLoginAsync(client, Password, "10.0.0.1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            using var fresh = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
            {
                Content = JsonContent.Create(new { login = Login, password = "a brand new passphrase 9" }),
            };
            SessionCookie(await client.SendAsync(fresh));
        }

        [Fact]
        public async Task PasswordChange_WrongCurrentPassword_CountsTowardLockout()
        {
            var client = await StartForLockoutAsync(o => o.LockoutThreshold = 3);
            for (var i = 0; i < 3; i++)
                (await ChangePasswordAsync(client, "wrong", "a brand new passphrase 9")).StatusCode
                    .Should().Be(HttpStatusCode.Unauthorized);

            (await PostLoginAsync(client, Password, "10.0.0.1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "guesses through the change endpoint lock the account like login guesses");
            (await ChangePasswordAsync(client, Password, "a brand new passphrase 9")).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized, "the change endpoint honours the lock too");
        }

        [Fact]
        public async Task MaxPasswordAge_ExpiresThePassword_UntilItIsChanged()
        {
            var client = await StartForLockoutAsync(o => o.MaxPasswordAge = TimeSpan.FromDays(90));

            using (var never = await PostLoginAsync(client, Password, "10.0.0.1"))
            {
                never.StatusCode.Should().Be(HttpStatusCode.Forbidden, "no recorded change date counts as expired");
                (await never.Content.ReadAsStringAsync()).Should().Contain("password_expired");
                never.Headers.Contains("Set-Cookie").Should().BeFalse("an expired password issues no session");
            }

            (await ChangePasswordAsync(client, Password, "a brand new passphrase 9")).StatusCode.Should().Be(HttpStatusCode.NoContent);
            using var fresh = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
            {
                Content = JsonContent.Create(new { login = Login, password = "a brand new passphrase 9" }),
            };
            SessionCookie(await client.SendAsync(fresh));

            _clock.Advance(TimeSpan.FromDays(91));
            using var stale = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
            {
                Content = JsonContent.Create(new { login = Login, password = "a brand new passphrase 9" }),
            };
            (await client.SendAsync(stale)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await AuditAsync()).Should().Contain(e => e.Reason == AuthAuditReasons.PasswordExpired);
        }

        [Fact]
        public void PolicyOptions_OutOfRange_FailAtStartup()
        {
            var cases = new Action<LocalAuthOptions>[]
            {
                o => o.SessionIdleTimeout = TimeSpan.Zero,
                o => o.SessionIdleTimeout = o.SessionLifetime + TimeSpan.FromSeconds(1),
                o => o.MaxConcurrentSessions = 0,
                o => o.LockoutThreshold = 0,
                o => o.LockoutDuration = TimeSpan.Zero,
                o => o.MinPasswordLength = 0,
                o => o.MaxPasswordLength = 11,
                o => o.PasswordHistoryCount = 25,
                o => o.MaxPasswordAge = TimeSpan.Zero,
            };
            foreach (var configure in cases)
            {
                var services = new ServiceCollection();
                var act = () => services.AddBifrostLocalAuth(ConnectionString, configure);
                act.Should().Throw<ArgumentOutOfRangeException>();
            }
        }

        [Fact]
        public async Task RateLimitedAttempts_AreAudited_WithoutTheTypedLoginName()
        {
            var client = await StartAsync(o =>
            {
                o.MaxLoginAttemptsPerSource = 100;
                o.MaxLoginAttemptsPerAccount = 1;
            });

            (await PostLoginAsync(client, "wrong", "10.0.0.9")).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "control: the first attempt is inside the window");
            (await PostLoginAsync(client, Password, "10.0.0.9")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            (await ChangePasswordAsync(client, Password, "a brand new passphrase 9")).StatusCode
                .Should().Be(HttpStatusCode.TooManyRequests);

            var audit = await AuditAsync();
            var refusals = audit.Where(e => e.Reason == AuthAuditReasons.RateLimited).ToList();
            refusals.Select(e => e.Kind).Should().BeEquivalentTo(new[]
            {
                AuthAuditKind.LoginFailed, AuthAuditKind.PasswordChangeRejected,
            }, "PCI DSS 10.2.1.4: a refused access attempt is an invalid access attempt");
            refusals.Should().OnlyContain(e => e.Outcome == AuthAuditOutcome.Failure && e.SubjectId == null,
                "the refusal runs before any account lookup, so it names no account");
            refusals.Single(e => e.Kind == AuthAuditKind.LoginFailed).SourceIp.Should().Be("10.0.0.9");
            refusals.Should().OnlyContain(e => e.ActorId == null && e.Reason != Login && e.SourceIp != Login,
                "the audit never records the login name typed");
        }

        [Theory]
        [InlineData("/auth/login", "application/x-www-form-urlencoded")]
        [InlineData("/auth/login", "application/json; charset=not-a-charset")]
        [InlineData("/auth/password", "text/plain")]
        [InlineData("/auth/password", "application/json; charset=not-a-charset")]
        public async Task ABodyThatIsNotJson_Is415_NotAServerError(string path, string contentType)
        {
            var client = await StartForLockoutAsync();
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent($$"""{"login":"{{Login}}","password":"x","currentPassword":"x","newPassword":"y"}"""),
            };
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        }

        [Fact]
        public async Task AdminEndpoint_UnknownCharset_Is415_NotAServerError()
        {
            var client = await StartForLockoutAsync();
            var admin = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/admin/unlock")
            {
                Content = new StringContent("""{"userId":"user-1"}"""),
            };
            request.Content.Headers.ContentType =
                System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json; charset=not-a-charset");
            request.Headers.Add("Cookie", admin);

            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        }

        [Theory]
        [InlineData("/auth/admin/revoke-sessions")]
        [InlineData("/auth/admin/unlock")]
        public async Task AdminEndpoint_RefusesAUserIdLongerThanTheAuditColumn_BeforeActing(string path)
        {
            var client = await StartForLockoutAsync();
            var admin = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));
            var longest = new string('u', SqlColumnDefinition.KeyTextMaxLength);

            (await PostAsync(client, path, admin, new { userId = longest })).StatusCode.Should().Be(HttpStatusCode.NoContent,
                "control: an id that fits the audit column is served");
            (await PostAsync(client, path, admin, new { userId = longest + "u" })).StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "an id the audit row cannot hold is refused before the action runs, not after it");
            (await AuditAsync()).Should().NotContain(e => e.SubjectId != null && e.SubjectId.Length > longest.Length);
        }

        [Fact]
        public void DurationOptions_PastTheCeiling_FailAtStartup_NotAtTheFirstLogin()
        {
            // now + SessionLifetime and now + LockoutDuration overflow DateTimeOffset for a huge value;
            // unchecked, that surfaced as a 500 on the first sign-in or the first lock.
            var ceiling = TimeSpan.FromDays(3650); // LocalAuthOptions.MaxDuration
            var cases = new (string Name, Action<LocalAuthOptions> Configure)[]
            {
                (nameof(LocalAuthOptions.SessionLifetime), o => o.SessionLifetime = TimeSpan.MaxValue),
                (nameof(LocalAuthOptions.SessionLifetime), o => o.SessionLifetime = ceiling + TimeSpan.FromTicks(1)),
                (nameof(LocalAuthOptions.LockoutDuration), o => o.LockoutDuration = TimeSpan.MaxValue),
                (nameof(LocalAuthOptions.LockoutDuration), o => o.LockoutDuration = ceiling + TimeSpan.FromTicks(1)),
            };
            foreach (var (name, configure) in cases)
            {
                var act = () => new ServiceCollection().AddBifrostLocalAuth(ConnectionString, configure);
                act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be(name);
            }

            var atCeiling = () => new ServiceCollection().AddBifrostLocalAuth(ConnectionString, o =>
            {
                o.SessionLifetime = ceiling;
                o.LockoutDuration = ceiling;
            });
            atCeiling.Should().NotThrow("control: the ceiling itself is a valid setting");
        }

        [Fact]
        public async Task LoginAudit_RecordsSuccessAndFailure_WithoutCredentials()
        {
            var client = await StartAsync(_ => { });
            (await PostLoginAsync(client, "wrong password", "10.0.0.5")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            SessionCookie(await PostLoginAsync(client, Password, "10.0.0.6"));

            var audit = await AuditAsync();

            audit.Should().Contain(e => e.Kind == AuthAuditKind.LoginFailed && e.SubjectId == "user-1"
                && e.SourceIp == "10.0.0.5" && e.Reason == AuthAuditReasons.BadCredentials && e.Outcome == AuthAuditOutcome.Failure);
            audit.Should().Contain(e => e.Kind == AuthAuditKind.LoginSucceeded && e.SubjectId == "user-1"
                && e.SourceIp == "10.0.0.6" && e.Outcome == AuthAuditOutcome.Success);
            var text = string.Join("|", audit.Select(e => e.ToString()));
            text.Should().NotContain(Password).And.NotContain("wrong password").And.NotContain(Login);
        }

        [Fact]
        public async Task DeletedUser_LosesAnIssuedSession()
        {
            var client = await StartAsync(o => o.SessionRevalidationInterval = TimeSpan.Zero);
            var cookie = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));
            (await GetSessionAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.OK,
                "control: the cookie authenticates while the row exists");

            Execute("DELETE FROM app_users WHERE id = 'user-1'");

            (await GetSessionAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task SessionLifetime_IsAbsolute_ActivityDoesNotExtendIt()
        {
            var client = await StartAsync(o =>
            {
                o.SessionLifetime = TimeSpan.FromMinutes(30);
                o.SessionIdleTimeout = TimeSpan.FromMinutes(30);
                o.SessionRevalidationInterval = TimeSpan.Zero;
            });
            var cookie = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));

            _clock.Advance(TimeSpan.FromMinutes(20));
            using (var active = await GetSessionAsync(client, cookie))
            {
                active.StatusCode.Should().Be(HttpStatusCode.OK, "control: the session is live inside its lifetime");
                // Revalidation re-issues the cookie; carry the newest one forward.
                if (active.Headers.TryGetValues("Set-Cookie", out var renewed))
                    cookie = renewed.Single().Split(';')[0];
            }

            _clock.Advance(TimeSpan.FromMinutes(11));
            (await GetSessionAsync(client, cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "31 minutes after sign-in the 30-minute session has ended, activity at minute 20 notwithstanding");
        }

        [Fact]
        public async Task WeakStoredHash_IsUpgradedOnSuccessfulLogin()
        {
            static PasswordHasher<string> Hasher(int iterations) => new(
                Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = iterations }));
            var weakHash = Hasher(1_000).HashPassword(Login, Password);
            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE app_users SET password_hash = @h WHERE id = 'user-1'";
                cmd.Parameters.AddWithValue("@h", weakHash);
                cmd.ExecuteNonQuery();
            }
            var current = Hasher(2_000);
            var store = new LocalUserStore(new SqliteDbConnFactory(ConnectionString), new LocalAuthOptions(), current);

            var verified = await store.VerifyCredentialsAsync(Login, Password);
            verified.Succeeded.Should().BeTrue();
            await store.UpgradePasswordHashAsync(verified, Login, Password, CancellationToken.None);

            string stored;
            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT password_hash FROM app_users WHERE id = 'user-1'";
                stored = (string)cmd.ExecuteScalar()!;
            }
            stored.Should().NotBe(weakHash, "a hash below the current work factor is replaced once the password verifies");
            current.VerifyHashedPassword(Login, stored, Password).Should().Be(PasswordVerificationResult.Success,
                "the replacement hash is at the current work factor and still matches the password");
        }

        [Fact]
        public async Task RehashOnLogin_DoesNotOverwriteAPasswordChangedBetweenVerifyAndUpgrade()
        {
            const string changed = "a changed passphrase 7";
            var racing = new RehashRacingHasher();
            var client = await StartAsync(_ => { }, services => services.AddSingleton(sp => new LocalUserStore(
                sp.GetRequiredService<IDbConnFactory>(), sp.GetRequiredService<LocalAuthOptions>(), racing)));
            // The concurrent change lands after the old password verified and before the upgrade
            // writes: the moment the upgrade hash is computed.
            racing.BeforeUpgradeHash = () =>
                Execute($"UPDATE app_users SET password_hash = '{CheapHasher.HashPassword(Login, changed)}' WHERE id = 'user-1'");

            (await PostLoginAsync(client, Password, "10.0.0.1")).StatusCode.Should().Be(HttpStatusCode.NoContent,
                "control: the old password verified before the change landed");
            racing.UpgradesComputed.Should().Be(1, "control: the login took the rehash branch");

            var stored = StoredHash();
            CheapHasher.VerifyHashedPassword(Login, stored, changed).Should().NotBe(PasswordVerificationResult.Failed,
                "the concurrent change wins; an unconditional upgrade put the old password back");
            CheapHasher.VerifyHashedPassword(Login, stored, Password).Should().Be(PasswordVerificationResult.Failed);
        }

        private string StoredHash()
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT password_hash FROM app_users WHERE id = 'user-1'";
            return (string)cmd.ExecuteScalar()!;
        }

        [Fact]
        public async Task RehashOnLogin_KeepsTheUsersOtherSessions_AndAuditsNoPasswordChange()
        {
            var hasher = new RehashRacingHasher { RehashDue = false };
            var client = await StartAsync(o => o.SessionRevalidationInterval = TimeSpan.Zero,
                services => services.AddSingleton(sp => new LocalUserStore(
                    sp.GetRequiredService<IDbConnFactory>(), sp.GetRequiredService<LocalAuthOptions>(), hasher)));
            var earlier = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));
            var before = StoredHash();

            hasher.RehashDue = true;
            SessionCookie(await PostLoginAsync(client, Password, "10.0.0.2"));
            StoredHash().Should().NotBe(before, "control: the second login rehashed the stored password");

            (await GetSessionAsync(client, earlier)).StatusCode.Should().Be(HttpStatusCode.OK,
                "a rehash keeps the same password, so it must not end the sessions signed in with it");
            var audit = await AuditAsync();
            audit.Should().NotContain(e => e.Reason == LocalSessionRevokeReasons.PasswordChanged);
            audit.Should().NotContain(e => e.Kind == AuthAuditKind.PasswordChanged);
        }

        [Fact]
        public async Task LockedAccount_RightPassword_DoesNotRehashTheStoredHash()
        {
            var hasher = new RehashRacingHasher();
            var client = await StartAsync(o =>
                {
                    o.MaxLoginAttemptsPerSource = 1_000;
                    o.MaxLoginAttemptsPerAccount = 1_000;
                    o.LockoutThreshold = 1;
                },
                services => services.AddSingleton(sp => new LocalUserStore(
                    sp.GetRequiredService<IDbConnFactory>(), sp.GetRequiredService<LocalAuthOptions>(), hasher)));
            (await PostLoginAsync(client, "wrong", "10.0.0.1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var before = StoredHash();

            (await PostLoginAsync(client, Password, "10.0.0.1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "control: the account is locked");

            StoredHash().Should().Be(before, "a login refused by the lockout must not rewrite the account's row");
            hasher.UpgradesComputed.Should().Be(0);
        }

        /// <summary>
        /// The in-memory state store with one seam: a callback that runs once, right after the first
        /// lockout read, so a fact can land a concurrent request's write between an endpoint's read
        /// of the lockout and its own write.
        /// </summary>
        private sealed class LockoutRacingStateStore : ILocalAuthStateStore
        {
            private readonly InMemoryLocalAuthStateStore _inner = new();
            private int _fired;

            public Func<ILocalAuthStateStore, string, Task>? AfterFirstLockoutRead { get; set; }

            public async Task<LocalLockoutState> GetLockoutAsync(string userId, CancellationToken cancellationToken)
            {
                var state = await _inner.GetLockoutAsync(userId, cancellationToken);
                if (AfterFirstLockoutRead is { } race && Interlocked.Exchange(ref _fired, 1) == 0)
                    await race(_inner, userId);
                return state;
            }

            public Task EnsureProvisionedAsync(CancellationToken ct) => _inner.EnsureProvisionedAsync(ct);
            public Task CreateSessionAsync(LocalSession session, CancellationToken ct) => _inner.CreateSessionAsync(session, ct);
            public Task<LocalSession?> GetSessionAsync(string sessionHash, CancellationToken ct) => _inner.GetSessionAsync(sessionHash, ct);
            public Task TouchSessionAsync(string sessionHash, DateTimeOffset lastSeenAt, CancellationToken ct) =>
                _inner.TouchSessionAsync(sessionHash, lastSeenAt, ct);
            public Task<bool> RevokeSessionAsync(string sessionHash, DateTimeOffset revokedAt, string reason, CancellationToken ct) =>
                _inner.RevokeSessionAsync(sessionHash, revokedAt, reason, ct);
            public Task<int> RevokeUserSessionsAsync(string userId, DateTimeOffset revokedAt, string reason, CancellationToken ct) =>
                _inner.RevokeUserSessionsAsync(userId, revokedAt, reason, ct);
            public Task<int> RebindSessionCredentialStampAsync(string userId, string fromStamp, string toStamp, CancellationToken ct) =>
                _inner.RebindSessionCredentialStampAsync(userId, fromStamp, toStamp, ct);
            public Task<IReadOnlyList<LocalSession>> ListLiveSessionsAsync(string userId, DateTimeOffset now, int maxRows, CancellationToken ct) =>
                _inner.ListLiveSessionsAsync(userId, now, maxRows, ct);
            public Task PurgeSessionsExpiredBeforeAsync(DateTimeOffset cutoff, CancellationToken ct) =>
                _inner.PurgeSessionsExpiredBeforeAsync(cutoff, ct);
            public Task<LocalFailedLoginResult> RecordFailedLoginAsync(
                string userId, DateTimeOffset at, int threshold, DateTimeOffset lockedUntil, CancellationToken ct) =>
                _inner.RecordFailedLoginAsync(userId, at, threshold, lockedUntil, ct);
            public Task ClearLockoutAsync(string userId, CancellationToken ct) => _inner.ClearLockoutAsync(userId, ct);
            public Task<bool> ClearExpiredLockoutAsync(string userId, DateTimeOffset now, CancellationToken ct) =>
                _inner.ClearExpiredLockoutAsync(userId, now, ct);
            public Task<bool> ClearFailuresUnlessLockedAsync(string userId, DateTimeOffset now, CancellationToken ct) =>
                _inner.ClearFailuresUnlessLockedAsync(userId, now, ct);
            public Task<IReadOnlyList<LocalPasswordHistoryEntry>> GetPasswordHistoryAsync(string userId, int maxEntries, CancellationToken ct) =>
                _inner.GetPasswordHistoryAsync(userId, maxEntries, ct);
            public Task AppendPasswordHistoryAsync(string userId, string passwordHash, DateTimeOffset changedAt, int keep, CancellationToken ct) =>
                _inner.AppendPasswordHistoryAsync(userId, passwordHash, changedAt, keep, ct);
            public Task AppendAuditAsync(AuthAuditEvent auditEvent, CancellationToken ct) => _inner.AppendAuditAsync(auditEvent, ct);
            public Task<IReadOnlyList<AuthAuditEvent>> ReadAuditAsync(DateTimeOffset since, int maxRows, CancellationToken ct) =>
                _inner.ReadAuditAsync(since, maxRows, ct);
        }

        /// <summary>
        /// The cheap hasher, reporting a successful verify as needing a rehash while
        /// <see cref="RehashDue"/> is set, with a hook run when the upgrade hash is computed (after
        /// verification, before the store writes it).
        /// </summary>
        private sealed class RehashRacingHasher : IPasswordHasher<string>
        {
            private int _armed;
            public Action? BeforeUpgradeHash { get; set; }
            public int UpgradesComputed { get; private set; }
            public bool RehashDue { get; set; } = true;

            public string HashPassword(string user, string password)
            {
                // The store's constructor hashes its timing decoy; only later calls are upgrades.
                if (Interlocked.Exchange(ref _armed, 1) == 1)
                {
                    UpgradesComputed++;
                    BeforeUpgradeHash?.Invoke();
                }
                return CheapHasher.HashPassword(user, password);
            }

            public PasswordVerificationResult VerifyHashedPassword(string user, string hashedPassword, string providedPassword) =>
                CheapHasher.VerifyHashedPassword(user, hashedPassword, providedPassword) == PasswordVerificationResult.Failed
                    ? PasswordVerificationResult.Failed
                    : RehashDue ? PasswordVerificationResult.SuccessRehashNeeded : PasswordVerificationResult.Success;
        }

        /// <summary>The PBKDF2 iteration count stored in an ASP.NET Core Identity V3 hash (bytes 5-8, big-endian).</summary>
        private static int V3Iterations(string hash)
        {
            var bytes = Convert.FromBase64String(hash);
            bytes[0].Should().Be(0x01, "an Identity V3 hash");
            return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(5, 4));
        }

        [Fact]
        public async Task DefaultStore_RehashesAFrameworkDefaultHash_AtTheOwaspIterationCount()
        {
            // The framework default (100,000 iterations of PBKDF2-HMAC-SHA512) is below OWASP's
            // 210,000. A hash written at it must verify and be lifted on the next login.
            var frameworkHash = new PasswordHasher<string>().HashPassword(Login, Password);
            V3Iterations(frameworkHash).Should().Be(100_000, "control: the framework default the store used to write");
            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE app_users SET password_hash = @h WHERE id = 'user-1'";
                cmd.Parameters.AddWithValue("@h", frameworkHash);
                cmd.ExecuteNonQuery();
            }
            var store = new LocalUserStore(new SqliteDbConnFactory(ConnectionString), new LocalAuthOptions());

            var verified = await store.VerifyCredentialsAsync(Login, Password);
            verified.Succeeded.Should().BeTrue();
            await store.UpgradePasswordHashAsync(verified, Login, Password, CancellationToken.None);

            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT password_hash FROM app_users WHERE id = 'user-1'";
                V3Iterations((string)cmd.ExecuteScalar()!).Should().Be(LocalAuthOptions.DefaultPasswordHashIterations);
            }
            LocalAuthOptions.DefaultPasswordHashIterations.Should().Be(210_000);
        }

        [Theory]
        [InlineData(99_999)]
        [InlineData(209_999)]
        public void IterationCount_BelowTheOwaspFigure_FailsAtStartup(int iterations)
        {
            var services = new ServiceCollection();

            var act = () => services.AddBifrostLocalAuth(ConnectionString, o => o.PasswordHashIterations = iterations);

            act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*PasswordHashIterations*at least 210000*");
        }

        [Fact]
        public void IterationCount_AtTheOwaspFigure_IsAccepted()
        {
            var services = new ServiceCollection();

            var act = () => services.AddBifrostLocalAuth(ConnectionString, o => o.PasswordHashIterations = 210_000);

            act.Should().NotThrow("control: the floor itself is a valid setting");
        }
    }
}
