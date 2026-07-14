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
    /// A real, transformer-pipeline RESP fixture: a seeded in-memory SQLite database behind the full
    /// BifrostQL endpoint stack (schema metadata rules, security transformers, the SQL-execution
    /// manager) plus a loopback RESP front door bound to that stack's real
    /// <see cref="IQueryIntentExecutor"/> / <see cref="IMutationIntentExecutor"/>. Commands run through
    /// this harness travel the ACTUAL RESP wire (its handler, codec, key parser, read/scan engines) into
    /// the real read pipeline, so tenant isolation and the generated SQL are exercised end to end —
    /// nothing is mocked. Mirrors <c>PgWireRealDbHarness</c>.
    ///
    /// <para>Each call opens its own authenticated connection for the given principal (a RESP session
    /// AUTHs per connection), so two identities are genuinely separate wire sessions. The generated SQL
    /// is captured per table so tests can assert the tenant predicate was injected by the pipeline.</para>
    /// </summary>
    internal sealed class RespRealDbHarness : IAsyncDisposable
    {
        private const string EndpointPath = "/graphql";
        private const string LoginUser = "u";
        private const string LoginSecret = "pw";

        private readonly string _connString;
        private readonly string[] _metadataRules;
        private readonly string[] _seedSql;
        private readonly bool _enableWrites;
        private readonly SqlCaptureObserver _sqlCapture = new();
        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;

        private RespRealDbHarness(string connName, string[] metadataRules, string[] seedSql, bool enableWrites)
        {
            _connString = $"Data Source=resp_{connName};Mode=Memory;Cache=Shared";
            _metadataRules = metadataRules;
            _seedSql = seedSql;
            _enableWrites = enableWrites;
        }

        public static async Task<RespRealDbHarness> StartAsync(
            string connName, string[] metadataRules, string[] seedSql, bool enableWrites = false)
        {
            var harness = new RespRealDbHarness(connName, metadataRules, seedSql, enableWrites);
            await harness.InitializeAsync();
            return harness;
        }

        private async Task InitializeAsync()
        {
            _keepAlive = new SqliteConnection(_connString);
            await _keepAlive.OpenAsync();
            foreach (var sql in _seedSql)
                await Exec(sql);

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
                        o.AddQueryObservers(new IQueryObserver[] { _sqlCapture });
                    });
                });
                web.Configure(_ => { });
            });
            _host = await builder.StartAsync();
        }

        /// <summary>Opens an authenticated loopback RESP session as <paramref name="principal"/> and runs one script.</summary>
        public async Task<T> RunAsync<T>(ClaimsPrincipal principal, Func<RespTestClient, Task<T>> script)
        {
            var store = new FakeRespCredentialStore().Add(LoginUser, LoginSecret, principal);
            var options = new RespWireOptions
            {
                RequireAuthentication = true,
                EnableWrites = _enableWrites,
                Endpoint = EndpointPath,
            };
            var handlerServices = new ServiceCollection()
                .AddSingleton(_host.Services.GetRequiredService<IQueryIntentExecutor>())
                .AddSingleton(_host.Services.GetRequiredService<IMutationIntentExecutor>())
                .AddSingleton(options)
                .BuildServiceProvider();

            await using var fixture = await RespFixture.StartAsync(store, handlerServices, options, RespDataHandlers.All());
            await RespWire.AuthenticateAsync(fixture.Client, LoginUser, LoginSecret);
            return await script(fixture.Client);
        }

        public Task<IReadOnlyList<string>> ScanKeysAsync(ClaimsPrincipal principal, string table) =>
            RunAsync(principal, client => RespWire.ScanAllKeysAsync(client, table));

        public Task<IReadOnlyDictionary<string, string?>> HGetAllAsync(ClaimsPrincipal principal, string key) =>
            RunAsync(principal, client => RespWire.HGetAllAsync(client, key));

        public Task<string?> GetAsync(ClaimsPrincipal principal, string key) =>
            RunAsync(principal, client => RespWire.GetAsync(client, key));

        /// <summary>All SQL the pipeline generated for a table (parameter placeholders, never inlined values).</summary>
        public string CapturedSql(string table) => string.Join("\n---\n", _sqlCapture.SqlFor(table));

        private async Task Exec(string sql)
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
            if (_keepAlive is not null)
                await _keepAlive.DisposeAsync();
        }

        /// <summary>Captures generated SQL per table at the AfterExecute phase (the phase carrying SQL text).</summary>
        private sealed class SqlCaptureObserver : IQueryObserver
        {
            private readonly object _gate = new();
            private readonly List<(string Table, string Sql)> _captured = new();

            public QueryPhase[] Phases { get; } = { QueryPhase.AfterExecute };

            public ValueTask OnQueryPhaseAsync(QueryPhase phase, QueryObserverContext context)
            {
                lock (_gate)
                    _captured.Add((context.Table.DbName, context.Sql ?? string.Empty));
                return ValueTask.CompletedTask;
            }

            public IReadOnlyList<string> SqlFor(string table)
            {
                lock (_gate)
                    return _captured
                        .Where(c => string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase))
                        .Select(c => c.Sql)
                        .ToArray();
            }
        }
    }
}
