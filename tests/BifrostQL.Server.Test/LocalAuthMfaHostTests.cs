using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Drives the local-auth TOTP second factor end to end through the production registrar
    /// (<c>AddBifrostLocalAuth</c> + <c>UseBifrostLocalAuth</c>) on a TestServer over a file-backed
    /// SQLite app-user table, with the field-encryption key seam and an injected clock that moves
    /// the TOTP time step.
    /// </summary>
    public sealed class LocalAuthMfaHostTests : IAsyncLifetime
    {
        private const string Alice = "alice@club.test"; // user-1, roles admin,editor
        private const string Bob = "bob@club.test";     // user-2, role viewer
        private const string Password = "correct horse battery";
        private const string TestIpHeader = "X-Test-Remote-Ip";
        private const string KeyRef = "config:mfa";

        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-localauth-mfa-{Guid.NewGuid():N}.db");
        private string ConnectionString => $"Data Source={_dbPath}";
        private WebApplication? _app;
        // A whole minute boundary, so "advance one step" never straddles two.
        private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        private readonly byte[] _rootKey = RandomNumberGenerator.GetBytes(FieldCipher.KeySize);
        private readonly InMemoryDataEncryptionKeyStore _dekStore = new();

        /// <summary>The 1,000-iteration fixture hasher of <see cref="LocalAuthLoginHostTests"/>, for the same reason.</summary>
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
                "INSERT INTO app_users VALUES ('user-1', @alice, @aliceHash, 'Alice', 'club-7', 'admin,editor');" +
                "INSERT INTO app_users VALUES ('user-2', @bob, @bobHash, 'Bob', 'club-7', 'viewer');";
            cmd.Parameters.AddWithValue("@alice", Alice);
            cmd.Parameters.AddWithValue("@aliceHash", CheapHasher.HashPassword(Alice, Password));
            cmd.Parameters.AddWithValue("@bob", Bob);
            cmd.Parameters.AddWithValue("@bobHash", CheapHasher.HashPassword(Bob, Password));
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

        /// <summary>
        /// Starts the host with MFA keyed (<see cref="KeyRef"/> over a config root key) and the
        /// shipped default <see cref="LocalAuthOptions.RequireMfa"/> = true, unless
        /// <paramref name="configure"/> says otherwise. A second call replaces the host over the same
        /// database and key store, as a restart would.
        /// </summary>
        private async Task<HttpClient> StartAsync(Action<LocalAuthOptions>? configure = null, bool registerKeys = true)
        {
            if (_app is not null)
                await _app.DisposeAsync();
            var builder = WebApplication.CreateBuilder();
            builder.Environment.EnvironmentName = Environments.Production;
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<TimeProvider>(_clock);
            if (registerKeys)
            {
                builder.Services.AddSingleton<IRootKeyProvider>(new ConfigRootKeyProvider(_rootKey));
                builder.Services.AddSingleton<IDataEncryptionKeyStore>(_dekStore);
            }
            builder.Services.AddBifrostLocalAuth(ConnectionString, o =>
            {
                o.MfaKeyRef = KeyRef;
                o.AdminRole = "admin";
                configure?.Invoke(o);
            });
            builder.Services.AddSingleton(sp => new LocalUserStore(
                sp.GetRequiredService<IDbConnFactory>(), sp.GetRequiredService<LocalAuthOptions>(), CheapHasher));
            _app = builder.Build();
            _app.Use((context, next) =>
            {
                if (context.Request.Headers.TryGetValue(TestIpHeader, out var ip))
                    context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                return next();
            });
            _app.UseAuthentication();
            _app.UseBifrostLocalAuth();
            // Stands in for every other mount (GraphQL, adapters): served only to a signed-in user.
            _app.Map("/protected", branch => branch.Run(context =>
            {
                context.Response.StatusCode = context.User.Identity?.IsAuthenticated == true
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }));
            await _app.StartAsync();
            return _app.GetTestClient();
        }

        private static async Task<HttpResponseMessage> SendAsync(
            HttpClient client, HttpMethod method, string path, string? cookie = null, object? body = null, string ip = "10.0.0.1")
        {
            using var request = new HttpRequestMessage(method, path);
            if (cookie is not null)
                request.Headers.Add("Cookie", cookie);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            request.Headers.Add(TestIpHeader, ip);
            return await client.SendAsync(request);
        }

        private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string login, string password = Password, string ip = "10.0.0.1") =>
            SendAsync(client, HttpMethod.Post, "/auth/login", body: new { login, password }, ip: ip);

        private static string Cookie(HttpResponseMessage response) =>
            response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal)
                                                               && !c.StartsWith(".AspNetCore.Cookies=;", StringComparison.Ordinal)).Split(';')[0];

        private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        private string Code(string secret, int offsetSteps = 0) =>
            Totp.Code(Base32.Decode(secret), Totp.TimeStep(_clock.GetUtcNow(), 30) + offsetSteps, LocalTotpAlgorithm.Sha1, 6);

        /// <summary>Moves the clock to the next time step, so the next code is fresh.</summary>
        private void NextStep() => _clock.Advance(TimeSpan.FromSeconds(30));

        private sealed record Enrolled(string Secret, IReadOnlyList<string> RecoveryCodes, string Cookie);

        /// <summary>Signs in with the password, enrolls, confirms, and steps the clock past the confirm code.</summary>
        private async Task<Enrolled> EnrollAsync(HttpClient client, string login)
        {
            var signIn = await LoginAsync(client, login);
            signIn.IsSuccessStatusCode.Should().BeTrue();
            var cookie = Cookie(signIn);
            var enroll = await SendAsync(client, HttpMethod.Post, "/auth/mfa/enroll", cookie, new { });
            enroll.StatusCode.Should().Be(HttpStatusCode.OK);
            var secret = (await JsonAsync(enroll)).GetProperty("secret").GetString()!;
            var confirm = await SendAsync(client, HttpMethod.Post, "/auth/mfa/enroll/confirm", cookie, new { code = Code(secret) });
            confirm.StatusCode.Should().Be(HttpStatusCode.OK);
            var codes = (await JsonAsync(confirm)).GetProperty("recoveryCodes").EnumerateArray().Select(e => e.GetString()!).ToList();
            var fullCookie = Cookie(confirm);
            NextStep();
            return new Enrolled(secret, codes, fullCookie);
        }

        private static async Task<string> ChallengeAsync(HttpClient client, string login, string ip = "10.0.0.1")
        {
            var response = await LoginAsync(client, login, ip: ip);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.Contains("Set-Cookie").Should().BeFalse("no session exists until the second factor verifies");
            var body = await JsonAsync(response);
            body.GetProperty("status").GetString().Should().Be(LocalLoginMfaResponse.MfaRequired);
            return body.GetProperty("challenge").GetString()!;
        }

        private static Task<HttpResponseMessage> VerifyAsync(
            HttpClient client, string challenge, string? code = null, string? recoveryCode = null, string ip = "10.0.0.1") =>
            SendAsync(client, HttpMethod.Post, "/auth/mfa/verify", body: new { challenge, code, recoveryCode }, ip: ip);

        private async Task<IReadOnlyList<AuthAuditEvent>> AuditAsync() =>
            await _app!.Services.GetRequiredService<ILocalAuthStateStore>()
                .ReadAuditAsync(DateTimeOffset.MinValue, 1_000, CancellationToken.None);

        private async Task<IReadOnlyList<LocalSession>> LiveSessionsAsync(string userId) =>
            await _app!.Services.GetRequiredService<ILocalAuthStateStore>()
                .ListLiveSessionsAsync(userId, _clock.GetUtcNow(), 100, CancellationToken.None);

        private void Execute(string sql)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        [Fact]
        public async Task EnrolledUser_SignsIn_OnlyWithPasswordThenCode_AndTheSessionRecordsBoth()
        {
            var client = await StartAsync();
            var enrolled = await EnrollAsync(client, Alice);

            var challenge = await ChallengeAsync(client, Alice);
            var verified = await VerifyAsync(client, challenge, code: Code(enrolled.Secret));

            verified.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await SendAsync(client, HttpMethod.Get, "/protected", Cookie(verified))).StatusCode.Should().Be(HttpStatusCode.OK);
            (await LiveSessionsAsync("user-1")).Should().Contain(s => s.Amr == "pwd otp" && !s.MfaPending);
            // One fake instant for every event, so assert membership, not order.
            (await AuditAsync()).Select(e => e.Kind).Should().Contain(new[] {
                AuthAuditKind.MfaEnrollmentStarted, AuthAuditKind.MfaEnabled, AuthAuditKind.MfaChallengeIssued,
                AuthAuditKind.MfaSucceeded, AuthAuditKind.LoginSucceeded });
            (await AuditAsync()).Should().Contain(e => e.Kind == AuthAuditKind.LoginSucceeded && e.Reason == AuthAuditReasons.Otp);
        }

        [Fact]
        public async Task OtpAuthUri_NamesIssuerAccountAndTheTotpParameters()
        {
            var client = await StartAsync();
            var cookie = Cookie(await LoginAsync(client, Alice));

            var body = await JsonAsync(await SendAsync(client, HttpMethod.Post, "/auth/mfa/enroll", cookie, new { }));

            var secret = body.GetProperty("secret").GetString()!;
            Base32.Decode(secret).Should().HaveCount(LocalMfaService.SecretBytes);
            body.GetProperty("otpAuthUri").GetString().Should().Be(
                $"otpauth://totp/BifrostQL:alice%40club.test?secret={secret}&issuer=BifrostQL&algorithm=SHA1&digits=6&period=30");
        }

        [Fact]
        public async Task WrongPassword_AnswersByteForByteAlike_WhetherOrNotTheAccountHasMfa()
        {
            var client = await StartAsync(o => o.RequireMfa = false);
            await EnrollAsync(client, Alice); // Bob stays password-only

            var enrolled = await LoginAsync(client, Alice, "wrong");
            var plain = await LoginAsync(client, Bob, "wrong");

            enrolled.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            plain.StatusCode.Should().Be(enrolled.StatusCode);
            (await enrolled.Content.ReadAsByteArrayAsync()).Should().Equal(await plain.Content.ReadAsByteArrayAsync());
            enrolled.Headers.Contains("Set-Cookie").Should().BeFalse();
            plain.Headers.Contains("Set-Cookie").Should().BeFalse();
        }

        [Fact]
        public async Task ACodeAccepted_IsRefusedTheSecondTime_InTheSameTimeStep()
        {
            var client = await StartAsync();
            var enrolled = await EnrollAsync(client, Alice);
            var code = Code(enrolled.Secret);

            (await VerifyAsync(client, await ChallengeAsync(client, Alice), code: code)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            var replay = await VerifyAsync(client, await ChallengeAsync(client, Alice), code: code);

            replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "PCI DSS 8.5.1: a TOTP code cannot be replayed");
            (await AuditAsync()).Should().Contain(e => e.Kind == AuthAuditKind.MfaFailed && e.Reason == AuthAuditReasons.MfaCodeReplayed);
        }

        [Fact]
        public async Task ConcurrentVerifies_WithOneCode_YieldExactlyOneSession()
        {
            var client = await StartAsync();
            var enrolled = await EnrollAsync(client, Alice);
            var code = Code(enrolled.Secret);
            var challenges = new List<string>();
            for (var i = 0; i < 6; i++)
                challenges.Add(await ChallengeAsync(client, Alice));

            var answers = await Task.WhenAll(challenges.Select(c => VerifyAsync(client, c, code: code)));

            answers.Count(r => r.StatusCode == HttpStatusCode.NoContent).Should().Be(1);
        }

        [Theory]
        [InlineData(-1, true)]
        [InlineData(1, true)]
        [InlineData(-2, false)]
        [InlineData(2, false)]
        public async Task ClockDrift_OfOneStep_IsAccepted_AndOfTwo_IsNot(int offset, bool accepted)
        {
            var client = await StartAsync();
            var enrolled = await EnrollAsync(client, Alice);
            _clock.Advance(TimeSpan.FromMinutes(10)); // far past the confirm step in both directions

            var answer = await VerifyAsync(client, await ChallengeAsync(client, Alice), code: Code(enrolled.Secret, offset));

            answer.StatusCode.Should().Be(accepted ? HttpStatusCode.NoContent : HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task RecoveryCode_SignsInOnce_IsAudited_AndCannotBeUsedAgain()
        {
            var client = await StartAsync();
            var enrolled = await EnrollAsync(client, Alice);
            enrolled.RecoveryCodes.Should().HaveCount(LocalAuthOptions.RecoveryCodeCount).And.OnlyHaveUniqueItems();
            var recovery = enrolled.RecoveryCodes[3];

            var first = await VerifyAsync(client, await ChallengeAsync(client, Alice), recoveryCode: recovery.ToLowerInvariant());
            var again = await VerifyAsync(client, await ChallengeAsync(client, Alice), recoveryCode: recovery);

            first.StatusCode.Should().Be(HttpStatusCode.NoContent, "dashes and case are not significant");
            again.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a recovery code is single use");
            (await LiveSessionsAsync("user-1")).Should().Contain(s => s.Amr == "pwd recovery");
            (await AuditAsync()).Should().ContainSingle(e => e.Kind == AuthAuditKind.MfaRecoveryCodeUsed && e.SubjectId == "user-1");
        }

        [Fact]
        public async Task UnenrolledUser_UnderRequireMfa_ReachesOnlyEnrollmentAndLogout()
        {
            var client = await StartAsync();

            var signIn = await LoginAsync(client, Alice);
            signIn.StatusCode.Should().Be(HttpStatusCode.OK);
            (await JsonAsync(signIn)).GetProperty("status").GetString().Should().Be(LocalLoginMfaResponse.MfaEnrollmentRequired);
            var pending = Cookie(signIn);

            (await SendAsync(client, HttpMethod.Get, "/protected", pending)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await SendAsync(client, HttpMethod.Get, "/PROTECTED", pending)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await SendAsync(client, HttpMethod.Post, "/auth/admin/unlock", pending, new { userId = "user-2" }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the admin role does not apply to an enrollment-only session");
            (await SendAsync(client, HttpMethod.Post, "/auth/mfa/enroll", pending, new { })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await LiveSessionsAsync("user-1")).Should().ContainSingle().Which.MfaPending.Should().BeTrue();
        }

        [Fact]
        public async Task SessionEndpoint_ForAnEnrollmentOnlySession_AnswersOnlyTheEnrollmentStatus()
        {
            var client = await StartAsync();
            var pending = Cookie(await LoginAsync(client, Alice));

            var session = await SendAsync(client, HttpMethod.Get, "/auth/session", pending);

            session.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await JsonAsync(session);
            body.GetProperty("status").GetString().Should().Be(LocalLoginMfaResponse.MfaEnrollmentRequired);
            // Nothing else: no identity field, no secret, no challenge.
            body.EnumerateObject().Select(p => p.Name).Should().Equal("status");
            // The status read grants nothing past it.
            (await SendAsync(client, HttpMethod.Get, "/protected", pending)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task SessionEndpoint_WithNoSession_OrAnExpiredEnrollmentOnlyOne_Answers401()
        {
            var client = await StartAsync(o => o.MfaEnrollmentTimeout = TimeSpan.FromMinutes(10));
            var pending = Cookie(await LoginAsync(client, Alice));

            (await SendAsync(client, HttpMethod.Get, "/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            // Control: the same cookie reads as enrollment-only before the timeout.
            (await SendAsync(client, HttpMethod.Get, "/auth/session", pending)).StatusCode.Should().Be(HttpStatusCode.OK);

            _clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

            var expired = await SendAsync(client, HttpMethod.Get, "/auth/session", pending);
            expired.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await expired.Content.ReadAsStringAsync()).Should().BeEmpty();
        }

        [Fact]
        public async Task SessionEndpoint_ForAFullSession_AnswersTheIdentity_AndNoStatus()
        {
            var client = await StartAsync();
            var enrolled = await EnrollAsync(client, Alice);

            var session = await SendAsync(client, HttpMethod.Get, "/auth/session", enrolled.Cookie);

            session.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await JsonAsync(session);
            body.GetProperty("id").GetString().Should().Be("user-1");
            body.GetProperty("email").GetString().Should().Be(Alice);
            body.TryGetProperty("status", out _).Should().BeFalse();
        }

        [Fact]
        public async Task PendingSession_EndsAfterTheEnrollmentTimeout()
        {
            var client = await StartAsync(o => o.MfaEnrollmentTimeout = TimeSpan.FromMinutes(10));
            var pending = Cookie(await LoginAsync(client, Alice));

            _clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

            (await SendAsync(client, HttpMethod.Post, "/auth/mfa/enroll", pending, new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task UnconfirmedEnrollment_Lapses()
        {
            var client = await StartAsync(o =>
            {
                o.RequireMfa = false;
                o.MfaEnrollmentTimeout = TimeSpan.FromMinutes(5);
            });
            var cookie = Cookie(await LoginAsync(client, Alice));
            var secret = (await JsonAsync(await SendAsync(client, HttpMethod.Post, "/auth/mfa/enroll", cookie, new { })))
                .GetProperty("secret").GetString()!;

            _clock.Advance(TimeSpan.FromMinutes(5));
            var confirm = await SendAsync(client, HttpMethod.Post, "/auth/mfa/enroll/confirm", cookie, new { code = Code(secret) });

            confirm.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await JsonAsync(confirm)).GetProperty("error").GetString().Should().Be("mfa_enrollment_expired");
            (await LoginAsync(client, Alice)).StatusCode.Should().Be(HttpStatusCode.NoContent, "an unconfirmed factor is never asked for");
        }

        [Fact]
        public async Task CodeAttempts_AreCappedPerAccount_BeforeTheCodeIsChecked()
        {
            var client = await StartAsync(o =>
            {
                o.MaxMfaAttemptsPerAccount = 4; // the enrollment confirm spends one
                o.MaxMfaAttemptsPerSource = 100;
                o.LockoutThreshold = 100;
            });
            var enrolled = await EnrollAsync(client, Alice);

            for (var i = 1; i <= 3; i++)
                (await VerifyAsync(client, await ChallengeAsync(client, Alice), code: "000000", ip: $"10.0.1.{i}"))
                    .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var right = await VerifyAsync(client, await ChallengeAsync(client, Alice), code: Code(enrolled.Secret), ip: "10.0.1.99");

            right.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "even the right code is not checked once the account window is full");
        }

        [Fact]
        public async Task WrongCodes_CountTowardTheLockout_AndALockedAccountRefusesTheRightCode()
        {
            var client = await StartAsync(o => o.LockoutThreshold = 3);
            var enrolled = await EnrollAsync(client, Alice);

            // Tickets taken before the lock: once locked, the password step itself refuses.
            var tickets = new List<string>();
            for (var i = 0; i < 4; i++)
                tickets.Add(await ChallengeAsync(client, Alice));
            for (var i = 0; i < 3; i++)
                (await VerifyAsync(client, tickets[i], code: "000000")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var right = await VerifyAsync(client, tickets[3], code: Code(enrolled.Secret));

            right.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var audit = await AuditAsync();
            audit.Should().Contain(e => e.Kind == AuthAuditKind.AccountLocked && e.SubjectId == "user-1");
            audit.Count(e => e.Kind == AuthAuditKind.MfaFailed && e.Reason == AuthAuditReasons.MfaCodeInvalid).Should().Be(3);
        }

        [Fact]
        public async Task Challenge_ExpiresAfterItsLifetime()
        {
            var client = await StartAsync();
            var enrolled = await EnrollAsync(client, Alice);
            var challenge = await ChallengeAsync(client, Alice);

            _clock.Advance(TimeSpan.FromMinutes(5));
            var late = await VerifyAsync(client, challenge, code: Code(enrolled.Secret));

            late.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await JsonAsync(late)).GetProperty("error").GetString().Should().Be("mfa_challenge_invalid");
        }

        [Fact]
        public async Task Challenge_IsSpentByItsSession_AndByTooManyWrongCodes()
        {
            var client = await StartAsync(o => o.MaxMfaChallengeAttempts = 2);
            var enrolled = await EnrollAsync(client, Alice);

            var used = await ChallengeAsync(client, Alice);
            (await VerifyAsync(client, used, code: Code(enrolled.Secret))).StatusCode.Should().Be(HttpStatusCode.NoContent);
            NextStep();
            var reuse = await VerifyAsync(client, used, code: Code(enrolled.Secret));
            (await JsonAsync(reuse)).GetProperty("error").GetString().Should().Be("mfa_challenge_invalid", "one ticket, one session");

            var guessed = await ChallengeAsync(client, Alice);
            (await VerifyAsync(client, guessed, code: "000000")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await VerifyAsync(client, guessed, code: "111111")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            NextStep();
            var after = await VerifyAsync(client, guessed, code: Code(enrolled.Secret));
            (await JsonAsync(after)).GetProperty("error").GetString().Should().Be("mfa_challenge_invalid");
        }

        [Fact]
        public async Task Challenge_IsVoided_ByAPasswordChangeSinceItWasIssued()
        {
            var client = await StartAsync();
            var enrolled = await EnrollAsync(client, Alice);
            var challenge = await ChallengeAsync(client, Alice);

            Execute($"UPDATE app_users SET password_hash = '{CheapHasher.HashPassword(Alice, "a different passphrase")}' WHERE id = 'user-1'");
            var answer = await VerifyAsync(client, challenge, code: Code(enrolled.Secret));

            answer.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await JsonAsync(answer)).GetProperty("error").GetString().Should().Be("mfa_challenge_invalid");
        }

        [Fact]
        public async Task UnknownChallenge_IsRefused_WithTheSameAnswerAsAnExpiredOne()
        {
            var client = await StartAsync();

            var answer = await VerifyAsync(client, LocalSessionId.New(), code: "123456");

            answer.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await answer.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"mfa_challenge_invalid\"}");
        }

        [Fact]
        public async Task Disable_NeedsTheCurrentFactor_AndEndsEverySession()
        {
            var client = await StartAsync(o => o.RequireMfa = false);
            var enrolled = await EnrollAsync(client, Alice);
            var other = Cookie(await VerifyAsync(client, await ChallengeAsync(client, Alice), code: Code(enrolled.Secret)));
            NextStep();

            (await SendAsync(client, HttpMethod.Post, "/auth/mfa/disable", enrolled.Cookie, new { code = "000000" }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await SendAsync(client, HttpMethod.Post, "/auth/mfa/disable", enrolled.Cookie, new { code = Code(enrolled.Secret) }))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);

            (await SendAsync(client, HttpMethod.Get, "/protected", other)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await SendAsync(client, HttpMethod.Get, "/protected", enrolled.Cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await LoginAsync(client, Alice)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await AuditAsync()).Should().Contain(e => e.Kind == AuthAuditKind.MfaDisabled && e.Outcome == AuthAuditOutcome.Success);
        }

        [Fact]
        public async Task RegeneratingRecoveryCodes_ReplacesTheOldOnes_AndEndsOtherSessions()
        {
            var client = await StartAsync();
            var enrolled = await EnrollAsync(client, Alice);
            var other = Cookie(await VerifyAsync(client, await ChallengeAsync(client, Alice), code: Code(enrolled.Secret)));
            NextStep();

            var regenerated = await SendAsync(client, HttpMethod.Post, "/auth/mfa/recovery-codes", enrolled.Cookie, new { code = Code(enrolled.Secret) });
            regenerated.StatusCode.Should().Be(HttpStatusCode.OK);
            var fresh = (await JsonAsync(regenerated)).GetProperty("recoveryCodes").EnumerateArray().Select(e => e.GetString()!).ToList();
            var caller = Cookie(regenerated);

            fresh.Should().HaveCount(LocalAuthOptions.RecoveryCodeCount).And.NotIntersectWith(enrolled.RecoveryCodes);
            (await SendAsync(client, HttpMethod.Get, "/protected", other)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await SendAsync(client, HttpMethod.Get, "/protected", caller)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await VerifyAsync(client, await ChallengeAsync(client, Alice), recoveryCode: enrolled.RecoveryCodes[0]))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await VerifyAsync(client, await ChallengeAsync(client, Alice), recoveryCode: fresh[0]))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        [Fact]
        public async Task AdminReset_RemovesTheFactor_EndsTheUsersSessions_AndIsAudited()
        {
            var client = await StartAsync(o => o.RequireMfa = false);
            var bob = await EnrollAsync(client, Bob);
            var admin = Cookie(await LoginAsync(client, Alice));

            var reset = await SendAsync(client, HttpMethod.Post, "/auth/admin/reset-mfa", admin, new { userId = "user-2" });

            reset.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await SendAsync(client, HttpMethod.Get, "/protected", bob.Cookie)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await LoginAsync(client, Bob)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await AuditAsync()).Should().Contain(e =>
                e.Kind == AuthAuditKind.MfaReset && e.SubjectId == "user-2" && e.ActorId == "user-1" && e.Outcome == AuthAuditOutcome.Success);
        }

        [Fact]
        public async Task AdminReset_IsRefused_ToANonAdministrator()
        {
            var client = await StartAsync(o => o.RequireMfa = false);
            var bob = Cookie(await LoginAsync(client, Bob));

            (await SendAsync(client, HttpMethod.Post, "/auth/admin/reset-mfa", bob, new { userId = "user-1" }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task PasswordOnlySession_OfAUserWhoMustUseMfa_IsRevoked()
        {
            var client = await StartAsync(o =>
            {
                o.RequireMfa = false;
                o.RequireMfaForRoles = new List<string> { "auditor" };
            });
            var bob = Cookie(await LoginAsync(client, Bob));
            (await SendAsync(client, HttpMethod.Get, "/protected", bob)).StatusCode.Should().Be(HttpStatusCode.OK, "control: viewer is not required");

            // The requirement now covers Bob's role, as after a config change; his session predates it.
            _app!.Services.GetRequiredService<LocalAuthOptions>().RequireMfaForRoles.Add("viewer");

            (await SendAsync(client, HttpMethod.Get, "/protected", bob)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await LiveSessionsAsync("user-2")).Should().BeEmpty();
            (await AuditAsync()).Should().Contain(e =>
                e.Kind == AuthAuditKind.SessionRevoked && e.Reason == LocalSessionRevokeReasons.MfaRequired);
        }

        [Fact]
        public async Task PasswordChange_ForAnEnrolledUser_NeedsTheSecondFactorToo()
        {
            var client = await StartAsync();
            var enrolled = await EnrollAsync(client, Alice);
            const string next = "another passphrase 42";

            var passwordOnly = await SendAsync(client, HttpMethod.Post, "/auth/password",
                body: new { login = Alice, currentPassword = Password, newPassword = next });
            var wrongCode = await SendAsync(client, HttpMethod.Post, "/auth/password",
                body: new { login = Alice, currentPassword = Password, newPassword = next, code = "000000" });
            var withCode = await SendAsync(client, HttpMethod.Post, "/auth/password",
                body: new { login = Alice, currentPassword = Password, newPassword = next, code = Code(enrolled.Secret) });

            passwordOnly.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the password alone cannot replace the password");
            wrongCode.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            withCode.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await LoginAsync(client, Alice, next)).StatusCode.Should().Be(HttpStatusCode.OK, "control: the change took effect");
        }

        [Fact]
        public async Task EnrolledFactor_FailsClosed_WhenTheMfaKeyIsRemovedFromConfig()
        {
            var client = await StartAsync(o => o.RequireMfa = false);
            await EnrollAsync(client, Alice);

            client = await StartAsync(o =>
            {
                o.RequireMfa = false;
                o.MfaKeyRef = null;
            }, registerKeys: false);
            var answer = await LoginAsync(client, Alice);

            answer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            answer.Headers.Contains("Set-Cookie").Should().BeFalse("dropping the key never skips the factor");
            (await JsonAsync(answer)).GetProperty("error").GetString().Should().Be("mfa_unavailable");
        }

        [Fact]
        public void RequireMfa_IsOnByDefault_AndWithoutAKey_FailsAtRegistration()
        {
            new LocalAuthOptions().RequireMfa.Should().BeTrue();
            var services = new ServiceCollection();

            var act = () => services.AddBifrostLocalAuth(ConnectionString);

            act.Should().Throw<InvalidOperationException>().WithMessage("*MfaKeyRef*RequireMfa = false*");
        }

        [Fact]
        public async Task MfaKey_WithoutAKeyManager_FailsTheHostStart()
        {
            var act = () => StartAsync(registerKeys: false);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*IRootKeyProvider*");
        }

        [Fact]
        public async Task StoredSecret_IsSealed_AndBoundToItsUser()
        {
            var client = await StartAsync();
            var enrolled = await EnrollAsync(client, Alice);
            var state = _app!.Services.GetRequiredService<ILocalAuthStateStore>();
            var row = (await state.GetMfaAsync("user-1", CancellationToken.None))!;

            row.SecretEnvelope.Should().NotContain(enrolled.Secret);
            row.RecoveryCodeHashes.Should().HaveCount(LocalAuthOptions.RecoveryCodeCount);
            row.RecoveryCodeHashes.Should().NotContain(h => enrolled.RecoveryCodes.Any(c => h.Contains(c.Replace("-", ""))));

            // The same envelope on Bob's row does not open: the AAD names the user.
            await state.InsertMfaAsync(row with { UserId = "user-2", Version = 1 }, CancellationToken.None);
            var bobChallenge = await ChallengeAsync(client, Bob);
            var act = () => VerifyAsync(client, bobChallenge, code: Code(enrolled.Secret));
            await act.Should().ThrowAsync<CryptographicException>();
        }

        [Fact]
        public async Task Audit_NeverCarriesTheSecret_ACode_OrARecoveryCode()
        {
            var client = await StartAsync();
            var enrolled = await EnrollAsync(client, Alice);
            var code = Code(enrolled.Secret);
            await VerifyAsync(client, await ChallengeAsync(client, Alice), code: code);
            await VerifyAsync(client, await ChallengeAsync(client, Alice), code: "000000");
            await VerifyAsync(client, await ChallengeAsync(client, Alice), recoveryCode: enrolled.RecoveryCodes[0]);

            var audit = JsonSerializer.Serialize(await AuditAsync());

            audit.Should().NotContain(enrolled.Secret).And.NotContain(code).And.NotContain("000000");
            foreach (var recovery in enrolled.RecoveryCodes)
                audit.Should().NotContain(recovery).And.NotContain(recovery.Replace("-", ""));
        }
    }
}
