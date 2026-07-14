using BifrostQL.Server.Resp;
using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// An HONEST real-client smoke: a genuine <see cref="ConnectionMultiplexer"/> from the
    /// StackExchange.Redis NuGet package connects to the BifrostQL RESP front door over a real loopback
    /// TCP socket (<see cref="RespTcpServerHarness"/>) — NO external Redis, NO faked replies — completes
    /// its full handshake (HELLO/AUTH/CLIENT/ECHO tracer), and reads through GET / HGETALL / SCAN against
    /// a seeded SQLite database behind the real transformer pipeline. Because SE.Redis's connection tracer
    /// is a binary ECHO, this exercises binary-safe ECHO and the whole plumbing surface exactly as a
    /// production Redis client would. It also proves tenant scoping holds for a real client: the connected
    /// identity is tenant-a, so tenant-b's row is invisible.
    ///
    /// <para>This is IN-PROCESS (the endpoint is a loopback <c>TcpListener</c> the test owns), so it needs
    /// no external service and stays in the default <c>dotnet test</c> gate. The end-to-end manual smoke
    /// against redis-cli / a standalone app is the runbook at <c>guides/resp-smoke.md</c>.</para>
    /// </summary>
    public sealed class RespStackExchangeSmokeTests : IAsyncLifetime
    {
        private const string LoginUser = "u";
        private const string LoginSecret = "pw";

        private static readonly string[] MetadataRules = { "*.orders { tenant-filter: tenant_id }" };
        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS orders",
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL)",
            """
            INSERT INTO orders(id, tenant_id, name) VALUES
                (1,'tenant-a','a-first'),(2,'tenant-a','a-second'),(3,'tenant-b','b-first')
            """,
        };

        private RespTcpServerHarness _server = null!;
        private ConnectionMultiplexer _mux = null!;
        private IDatabase _db = null!;

        public async Task InitializeAsync()
        {
            var store = new FakeRespCredentialStore()
                .Add(LoginUser, LoginSecret, RespTcpServerHarness.TenantPrincipal("user-a", "tenant-a"));
            var options = new RespWireOptions { RequireAuthentication = true, EnableWrites = false, Endpoint = "/graphql" };
            _server = await RespTcpServerHarness.StartAsync(nameof(RespStackExchangeSmokeTests), MetadataRules, SeedSql, store, options);

            var config = new ConfigurationOptions
            {
                EndPoints = { { "127.0.0.1", _server.Port } },
                User = LoginUser,
                Password = LoginSecret,
                AbortOnConnectFail = false,
                ConnectTimeout = 5000,
                ConnectRetry = 2,
                SyncTimeout = 5000,
            };
            _mux = await ConnectionMultiplexer.ConnectAsync(config);
            _db = _mux.GetDatabase();
        }

        public async Task DisposeAsync()
        {
            if (_mux is not null) await _mux.DisposeAsync();
            await _server.DisposeAsync();
        }

        [Fact]
        public void RealClient_CompletesHandshake_AndConnects()
        {
            // A real StackExchange.Redis multiplexer reached a connected state — the HELLO/AUTH/CLIENT/
            // binary-ECHO handshake all answered. PING confirms the interactive connection round-trips.
            _mux.IsConnected.Should().BeTrue("the real Redis client must complete its connection handshake");
            _db.Ping().Should().BePositive();
        }

        [Fact]
        public async Task RealClient_HGetAll_ReturnsOwnTenantRow_AsFieldHash()
        {
            var hash = await _db.HashGetAllAsync("orders:1");
            var map = hash.ToDictionary(e => (string)e.Name!, e => (string?)e.Value);
            map.Should().Contain("name", "a-first").And.Contain("tenant_id", "tenant-a");
        }

        [Fact]
        public async Task RealClient_Get_ReturnsRowJson()
        {
            var value = await _db.StringGetAsync("orders:2");
            value.HasValue.Should().BeTrue();
            ((string)value!).Should().Contain("\"name\":\"a-second\"");
        }

        [Fact]
        public async Task RealClient_Scan_EnumeratesOnlyOwnTenantKeys()
        {
            var result = await _db.ExecuteAsync("SCAN", "0", "MATCH", "orders:*", "COUNT", "100");
            var page = (RedisResult[])result!;
            var keys = ((RedisResult[])page[1]!).Select(r => (string)r!).ToList();

            // The connected identity is tenant-a: SCAN over a real client enumerates only its rows.
            keys.Should().BeEquivalentTo("orders:1", "orders:2");
            keys.Should().NotContain("orders:3", "tenant-b's key must be invisible to tenant-a's real client");
        }

        [Fact]
        public async Task RealClient_HGetAll_OnOtherTenantRow_IsEmpty_NoLeak()
        {
            // orders:3 belongs to tenant-b; the tenant filter makes it indistinguishable from missing.
            var hash = await _db.HashGetAllAsync("orders:3");
            hash.Should().BeEmpty("a row in another tenant must be invisible to a real Redis client");
        }
    }
}
