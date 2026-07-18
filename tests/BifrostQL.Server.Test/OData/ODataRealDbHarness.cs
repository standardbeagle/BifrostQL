using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// A real, transformer-pipeline OData fixture: a seeded in-memory SQLite database behind the full
    /// BifrostQL endpoint stack (schema metadata rules, policy transformer, dialect type mapper, FK
    /// relationship detection) exposing that stack's real <see cref="IQueryIntentExecutor"/>. The
    /// OData metadata generators resolve the SAME cached <see cref="IDbModel"/> the query path uses,
    /// so key/type/nullability/navigation projection and identity filtering are exercised against a
    /// genuine dialect-mapped model — nothing about the schema shape is mocked. Mirrors
    /// <c>S3ListingRealDbHarness</c>.
    /// </summary>
    internal sealed class ODataRealDbHarness : IAsyncDisposable
    {
        public const string EndpointPath = "/graphql";

        private readonly string _connString;
        private readonly string[] _metadataRules;
        private readonly string[] _seedSql;
        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;

        private ODataRealDbHarness(string connName, string[] metadataRules, string[] seedSql)
        {
            // Per-instance token: Cache=Shared keys the in-memory DB on the Data Source name
            // process-wide, so a stable name would leak a seeded DB across harness instances.
            _connString = $"Data Source=odata_{connName}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            _metadataRules = metadataRules;
            _seedSql = seedSql;
        }

        public static async Task<ODataRealDbHarness> StartAsync(
            string connName, string[] metadataRules, string[] seedSql)
        {
            var harness = new ODataRealDbHarness(connName, metadataRules, seedSql);
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
                    });
                });
                web.Configure(_ => { });
            });
            _host = await builder.StartAsync();
        }

        public IQueryIntentExecutor Reads => _host.Services.GetRequiredService<IQueryIntentExecutor>();

        public Task<IDbModel> ModelAsync() => Reads.GetModelAsync(EndpointPath);

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

        private async Task Exec(string sql)
        {
            await using var cmd = _keepAlive.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
