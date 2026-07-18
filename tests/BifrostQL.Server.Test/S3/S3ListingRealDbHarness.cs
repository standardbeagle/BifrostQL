using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Storage;
using BifrostQL.Server.S3;
using BifrostQL.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// A real, transformer-pipeline S3-listing fixture: a seeded in-memory SQLite database behind the
    /// full BifrostQL endpoint stack (schema metadata rules, tenant/soft-delete/policy transformers,
    /// the SQL-execution manager) plus an <see cref="S3Listing"/> bound to that stack's real
    /// <see cref="IQueryIntentExecutor"/>. Listing through this harness travels the ACTUAL read
    /// pipeline, so tenant isolation, soft-delete exclusion, and policy read-gating are exercised end
    /// to end — nothing is mocked. Mirrors <c>RespRealDbHarness</c>.
    /// </summary>
    internal sealed class S3ListingRealDbHarness : IAsyncDisposable
    {
        private const string EndpointPath = "/graphql";

        private readonly string _connString;
        private readonly string[] _metadataRules;
        private readonly string[] _seedSql;
        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;

        private S3ListingRealDbHarness(string connName, string[] metadataRules, string[] seedSql)
        {
            // Unique per harness instance: Cache=Shared keys the in-memory DB on the Data Source
            // name process-wide, so reusing a name across instances (every test method rebuilds the
            // harness with the same connName) leaks a seeded DB when the host's connection pool
            // outlives DisposeAsync — the next re-seed then hits "table already exists". A per-instance
            // token isolates each harness while keepAlive + host still share this instance's DB.
            _connString = $"Data Source=s3list_{connName}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            _metadataRules = metadataRules;
            _seedSql = seedSql;
        }

        public static async Task<S3ListingRealDbHarness> StartAsync(
            string connName, string[] metadataRules, string[] seedSql)
        {
            var harness = new S3ListingRealDbHarness(connName, metadataRules, seedSql);
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

        public IMutationIntentExecutor Writes => _host.Services.GetRequiredService<IMutationIntentExecutor>();

        /// <summary>Builds a lister over the real executor with the given options (default options when null).</summary>
        public S3Listing Listing(S3Options? options = null)
            => new(Reads, options ?? new S3Options { Endpoint = EndpointPath });

        /// <summary>
        /// Builds a file-object seam over the real read/write pipeline. A caller that
        /// needs a real storage backing (GetObject) supplies a <see cref="FileStorageService"/>
        /// bound to a temp directory; the read-only list/routing tests can omit it.
        /// </summary>
        public FileObjectSeam Seam(FileStorageService? storage = null, S3Options? options = null, bool enableWrites = false)
            => new(Reads, Writes, storage, new FileObjectSeamOptions
            {
                Endpoint = (options ?? new S3Options()).Endpoint ?? EndpointPath,
                EnableWrites = enableWrites,
            });

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
