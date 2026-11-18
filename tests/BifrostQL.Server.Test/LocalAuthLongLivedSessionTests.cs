using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
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
                "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT);" +
                "INSERT INTO widgets VALUES (1, 'sprocket');";
            cmd.Parameters.AddWithValue("@email", Login);
            cmd.Parameters.AddWithValue("@hash", CheapHasher.HashPassword(Login, Password));
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

        private async Task<HttpClient> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddBifrostLocalAuth(ConnectionString);
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

        private async Task<WebSocket> ConnectAsync(string cookie, CancellationToken ct)
        {
            var wsClient = _app!.GetTestServer().CreateWebSocketClient();
            wsClient.ConfigureRequest = req => req.Headers["Cookie"] = cookie;
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
    }
}
