using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BifrostQL.Core.Model;
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
            cmd.Parameters.AddWithValue("@hash", new PasswordHasher<string>().HashPassword(Login, Password));
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
        public async Task RoleChange_ReachesAnIssuedSession()
        {
            var client = await StartAsync(o => o.SessionRevalidationInterval = TimeSpan.Zero);
            var cookie = SessionCookie(await PostLoginAsync(client, Password, "10.0.0.1"));

            Execute("UPDATE app_users SET roles = 'member', tenant_id = 'club-9' WHERE id = 'user-1'");

            using var session = await GetSessionAsync(client, cookie);
            session.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("roles").EnumerateArray().Select(e => e.GetString())
                .Should().BeEquivalentTo(new[] { "member" }, "the admin role was removed from the row");
            doc.RootElement.GetProperty("tenantId").GetString().Should().Be("club-9");
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

            (await store.VerifyCredentialsAsync(Login, Password)).Succeeded.Should().BeTrue();

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

            (await store.VerifyCredentialsAsync(Login, Password)).Succeeded.Should().BeTrue();

            using (var conn = new SqliteConnection(ConnectionString))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT password_hash FROM app_users WHERE id = 'user-1'";
                V3Iterations((string)cmd.ExecuteScalar()!).Should().Be(LocalAuthOptions.DefaultPasswordHashIterations);
            }
            LocalAuthOptions.DefaultPasswordHashIterations.Should().Be(210_000);
        }

        [Fact]
        public void IterationCount_BelowTheFrameworkDefault_FailsAtStartup()
        {
            var services = new ServiceCollection();

            var act = () => services.AddBifrostLocalAuth(ConnectionString, o => o.PasswordHashIterations = 99_999);

            act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*PasswordHashIterations*");
        }
    }
}
