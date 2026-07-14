using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Resp;
using BifrostQL.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// A real RESP endpoint on a loopback <see cref="TcpListener"/> — a genuine, dial-able TCP port — in
    /// front of a seeded SQLite database behind the full BifrostQL endpoint stack. Unlike
    /// <see cref="RespRealDbHarness"/> (an in-process socket pair for our own codec), this accepts
    /// arbitrary external clients, so a real StackExchange.Redis <c>ConnectionMultiplexer</c> can connect
    /// over the network stack and exercise the handshake + data path exactly as a production Redis client
    /// would. An accept loop pumps <see cref="RespConnectionHandler"/> per connection (SE.Redis opens
    /// several), closing each socket when the handler returns — Kestrel's connection-close semantics.
    /// </summary>
    internal sealed class RespTcpServerHarness : IAsyncDisposable
    {
        private const string EndpointPath = "/graphql";

        private readonly string _connString;
        private readonly string[] _metadataRules;
        private readonly string[] _seedSql;
        private readonly IRespCredentialStore _credentials;
        private readonly RespWireOptions _options;
        private readonly CancellationTokenSource _cts = new();

        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;
        private TcpListener _listener = null!;
        private Task _acceptLoop = null!;

        public int Port { get; private set; }

        private RespTcpServerHarness(
            string connName, string[] metadataRules, string[] seedSql,
            IRespCredentialStore credentials, RespWireOptions options)
        {
            _connString = $"Data Source=resptcp_{connName};Mode=Memory;Cache=Shared";
            _metadataRules = metadataRules;
            _seedSql = seedSql;
            _credentials = credentials;
            _options = options;
        }

        public static async Task<RespTcpServerHarness> StartAsync(
            string connName, string[] metadataRules, string[] seedSql,
            IRespCredentialStore credentials, RespWireOptions options)
        {
            var harness = new RespTcpServerHarness(connName, metadataRules, seedSql, credentials, options);
            await harness.InitializeAsync();
            return harness;
        }

        private async Task InitializeAsync()
        {
            _keepAlive = new SqliteConnection(_connString);
            await _keepAlive.OpenAsync();
            foreach (var sql in _seedSql)
            {
                await using var cmd = new SqliteCommand(sql, _keepAlive);
                await cmd.ExecuteNonQueryAsync();
            }

            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddBifrostEndpoints(o =>
                    {
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = _connString;
                            e.Provider = "sqlite";
                            e.Path = EndpointPath;
                            e.Metadata = _metadataRules;
                            e.DisableAuth = true;
                        });
                    });
                });
                web.Configure(_ => { });
            });
            _host = await builder.StartAsync();

            var handlerServices = new ServiceCollection()
                .AddSingleton(_host.Services.GetRequiredService<IQueryIntentExecutor>())
                .AddSingleton(_host.Services.GetRequiredService<IMutationIntentExecutor>())
                .AddSingleton(_options)
                .BuildServiceProvider();
            var handler = new RespConnectionHandler(
                _credentials, BifrostAuthContextFactory.Instance, handlerServices, _options, RespDataHandlers.All());

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(handler, _cts.Token));
        }

        private async Task AcceptLoopAsync(RespConnectionHandler handler, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient socket;
                try
                {
                    socket = await _listener.AcceptTcpClientAsync(ct);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return; // listener stopped
                }

                _ = Task.Run(async () =>
                {
                    try { await handler.HandleConnectionAsync(socket.GetStream(), ct); }
                    catch { /* per-connection faults are contained; never tear down the accept loop */ }
                    finally { socket.Close(); }
                }, ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener?.Stop();
            try { if (_acceptLoop is not null) await _acceptLoop; } catch { /* expected on shutdown */ }
            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
            if (_keepAlive is not null)
                await _keepAlive.DisposeAsync();
            _cts.Dispose();
        }

        public static ClaimsPrincipal TenantPrincipal(string userId, string tenantId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(BifrostQL.Server.Auth.LocalAuthClaims.Tenant, tenantId),
            }, authenticationType: "resp"));
    }
}
