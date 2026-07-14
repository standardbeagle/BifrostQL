using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// A real, transformer-pipeline pgwire fixture: a seeded in-memory SQLite database behind
    /// the full BifrostQL endpoint stack (schema metadata rules, security transformers, the
    /// SQL-execution manager) plus a loopback pgwire front door bound to that stack's real
    /// <see cref="IQueryIntentExecutor"/>. Queries run through <see cref="PgWireLoopback"/>
    /// travel the actual pgwire wire into the real read pipeline, so tenant isolation and the
    /// generated SQL are exercised end to end — nothing is mocked.
    ///
    /// <para>The generated SQL is captured per table so tests can assert the tenant predicate
    /// was injected by the pipeline (not authored by the wire codec) and that caller values
    /// bind as parameters.</para>
    /// </summary>
    internal sealed class PgWireRealDbHarness : IAsyncDisposable
    {
        private const string EndpointPath = "/graphql";
        private readonly string _connString;
        private readonly string[] _metadataRules;
        private readonly string[] _seedSql;
        private readonly SqlCaptureObserver _sqlCapture = new();
        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;

        public PgWireRealDbHarness(string connName, string[] metadataRules, string[] seedSql)
        {
            _connString = $"Data Source=pgwire_{connName};Mode=Memory;Cache=Shared";
            _metadataRules = metadataRules;
            _seedSql = seedSql;
        }

        public static async Task<PgWireRealDbHarness> StartAsync(string connName, string[] metadataRules, string[] seedSql)
        {
            var harness = new PgWireRealDbHarness(connName, metadataRules, seedSql);
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

        private IQueryIntentExecutor Executor => _host.Services.GetRequiredService<IQueryIntentExecutor>();

        /// <summary>Runs a statement over the real pgwire wire as <paramref name="principal"/>.</summary>
        public Task<SimpleQueryResult> QueryAsync(ClaimsPrincipal principal, string sql)
            => PgWireLoopback.RunAsync(Executor, principal, sql, EndpointPath, TimeSpan.FromSeconds(20));

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
