using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// A local-auth session must stop working on a long-lived connection the moment it ends, not when
    /// the connection does. The cookie hook only runs when an HTTP request starts, so an open binary
    /// WebSocket kept querying after logout, a password change or an administrator revoke.
    /// </summary>
    public sealed class LocalAuthLongLivedSessionTests : IAsyncLifetime
    {
        private const string Login = "alice@club.test";
        private const string Password = "correct horse battery 1";
        private const string SocketPath = "/bifrost-ws";
        private const string Bob = "bob@club.test"; // user-2, role viewer: owes a second factor in the MFA host
        private const string UncheckedCookieHeader = "X-Test-Unchecked-Cookie";

        private static readonly PasswordHasher<string> CheapHasher = new(
            Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));

        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-longlived-{Guid.NewGuid():N}.db");
        private WebApplication? _app;

        private string ConnectionString => $"Data Source={_dbPath}";

        public async Task InitializeAsync()
        {
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE app_users (id TEXT PRIMARY KEY, email TEXT, password_hash TEXT, " +
                "display_name TEXT, tenant_id TEXT, roles TEXT);" +
                "INSERT INTO app_users VALUES ('user-1', @email, @hash, 'Alice', 'club-7', 'editor');" +
                "INSERT INTO app_users VALUES ('user-2', @bob, @bobHash, 'Bob', 'club-7', 'viewer');" +
                "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT);" +
                "INSERT INTO widgets VALUES (1, 'sprocket');";
            cmd.Parameters.AddWithValue("@email", Login);
            cmd.Parameters.AddWithValue("@hash", CheapHasher.HashPassword(Login, Password));
            cmd.Parameters.AddWithValue("@bob", Bob);
            cmd.Parameters.AddWithValue("@bobHash", CheapHasher.HashPassword(Bob, Password));
            await cmd.ExecuteNonQueryAsync();
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
        /// Starts the host. <paramref name="viewersOweMfa"/> keys MFA and requires it of the viewer
        /// role only, so Bob signs in to an enrollment-only session and Alice to a full one; it also
        /// mounts a test hook that adopts the principal of a cookie sent in
        /// <see cref="UncheckedCookieHeader"/> without the cookie hook, so the socket's own
        /// per-message check is what decides.
        /// </summary>
        private async Task<HttpClient> StartAsync(bool viewersOweMfa = false)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Environment.EnvironmentName = Microsoft.Extensions.Hosting.Environments.Production;
            builder.WebHost.UseTestServer();
            if (viewersOweMfa)
            {
                builder.Services.AddSingleton<IRootKeyProvider>(new ConfigRootKeyProvider(RandomNumberGenerator.GetBytes(FieldCipher.KeySize)));
                builder.Services.AddSingleton<IDataEncryptionKeyStore>(new InMemoryDataEncryptionKeyStore());
            }
            builder.Services.AddBifrostLocalAuth(ConnectionString, o =>
            {
                o.RequireMfa = false;
                if (viewersOweMfa)
                {
                    o.MfaKeyRef = "config:mfa";
                    o.RequireMfaForRoles = new List<string> { "viewer" };
                }
            });
            builder.Services.AddSingleton(sp => new LocalUserStore(
                sp.GetRequiredService<IDbConnFactory>(), sp.GetRequiredService<LocalAuthOptions>(), CheapHasher));
            builder.Services.AddBifrostEndpoints(o => o.AddEndpoint(e =>
            {
                e.ConnectionString = ConnectionString;
                e.Provider = "sqlite";
                e.Path = "/graphql";
                e.DisableAuth = true;
            }));
            builder.Services.AddBifrostEngine();
            _app = builder.Build();
            _app.UseWebSockets();
            _app.UseAuthentication();
            if (viewersOweMfa)
                _app.Use((context, next) =>
                {
                    if (context.Request.Headers.TryGetValue(UncheckedCookieHeader, out var raw))
                    {
                        var format = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                            .Get(CookieAuthenticationDefaults.AuthenticationScheme).TicketDataFormat;
                        context.User = format.Unprotect(raw.ToString().Split('=', 2)[1])!.Principal;
                    }
                    return next();
                });
            _app.UseBifrostLocalAuth();
            _app.UseBifrostEndpoints();
            _app.UseBifrostBinary(SocketPath, graphqlPath: "/graphql");
            await _app.StartAsync();
            return _app.GetTestClient();
        }

        private async Task<string> SignInAsync(HttpClient client)
        {
            using var login = await client.PostAsJsonAsync("/auth/login", new { login = Login, password = Password });
            login.StatusCode.Should().Be(HttpStatusCode.NoContent);
            return login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        }

        private async Task<WebSocket> ConnectAsync(string cookie, CancellationToken ct, bool skipCookieHook = false)
        {
            var wsClient = _app!.GetTestServer().CreateWebSocketClient();
            wsClient.ConfigureRequest = req => req.Headers[skipCookieHook ? UncheckedCookieHeader : "Cookie"] = cookie;
            return await wsClient.ConnectAsync(new Uri($"ws://localhost{SocketPath}"), ct);
        }

        /// <summary>Sends one Query frame and returns the first frame back, or the close status when the server closed.</summary>
        private static async Task<(BifrostMessage? Frame, WebSocketCloseStatus? Close)> QueryAsync(
            WebSocket socket, uint requestId, CancellationToken ct)
        {
            var request = new BifrostMessage
            {
                RequestId = requestId,
                Type = BifrostMessageType.Query,
                Query = "{ widgets { data { id name } } }",
            };
            await socket.SendAsync(new ArraySegment<byte>(request.ToBytes()), WebSocketMessageType.Binary, true, ct);
            var buffer = new byte[64 * 1024];
            try
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                return result.MessageType == WebSocketMessageType.Close
                    ? (null, result.CloseStatus)
                    : (BifrostMessage.FromBytes(buffer.AsSpan(0, result.Count).ToArray()), null);
            }
            catch (WebSocketException)
            {
                return (null, socket.CloseStatus);
            }
        }

        public static IEnumerable<object[]> SessionEndings => new[]
        {
            new object[] { "administrator-revoke" },
            new object[] { "logout" },
        };

        [Theory]
        [MemberData(nameof(SessionEndings))]
        public async Task OpenBinarySocket_StopsServingTheMessageAfterTheSessionEnds_AndCloses(string ending)
        {
            var client = await StartAsync();
            var cookie = await SignInAsync(client);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = await ConnectAsync(cookie, cts.Token);

            var before = await QueryAsync(socket, 1, cts.Token);
            before.Frame.Should().NotBeNull("control: a live session is served over the socket");
            before.Frame!.Type.Should().Be(BifrostMessageType.Result);
            System.Text.Encoding.UTF8.GetString(before.Frame.Payload).Should().Contain("sprocket");

            if (ending == "logout")
            {
                using var logout = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
                logout.Headers.Add("Cookie", cookie);
                (await client.SendAsync(logout)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            }
            else
            {
                await _app!.Services.GetRequiredService<LocalAuthAdministration>()
                    .RevokeAllSessionsAsync("user-1", "admin-1", CancellationToken.None);
            }

            var after = await QueryAsync(socket, 2, cts.Token);
            after.Frame.Should().BeNull("a message sent after the session ended must not be answered");
            after.Close.Should().Be(WebSocketCloseStatus.PolicyViolation);
        }

        [Theory]
        [InlineData(Login, true)]
        [InlineData(Bob, false)]
        public async Task OpenBinarySocket_RefusesAnEnrollmentOnlySession_AtTheFirstMessage(string login, bool served)
        {
            // An enrollment-only session (Bob: a viewer who owes a second factor he has not enrolled)
            // may reach the enrollment and logout endpoints and nothing else. The cookie hook already
            // runs the upgrade request anonymous; the socket must not lean on that, so the test hook
            // hands it Bob's principal unchecked and the per-message check alone must refuse it.
            var client = await StartAsync(viewersOweMfa: true);
            using var signIn = await client.PostAsJsonAsync("/auth/login", new { login, password = Password });
            signIn.IsSuccessStatusCode.Should().BeTrue();
            var cookie = signIn.Headers.GetValues("Set-Cookie")
                .Single(c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal)).Split(';')[0];
            if (!served)
                (await signIn.Content.ReadAsStringAsync()).Should().Contain("mfa-enrollment-required");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = await ConnectAsync(cookie, cts.Token, skipCookieHook: true);
            var reply = await QueryAsync(socket, 1, cts.Token);

            if (served)
            {
                reply.Frame.Should().NotBeNull("control: a full session handed over the same hook is served");
                System.Text.Encoding.UTF8.GetString(reply.Frame!.Payload).Should().Contain("sprocket");
                return;
            }
            reply.Frame.Should().BeNull("an enrollment-only session must not be answered over the socket");
            reply.Close.Should().Be(WebSocketCloseStatus.PolicyViolation);
        }
    }
}
