using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Prometheus;
using BifrostQL.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Integration.Test.Prometheus
{
    /// <summary>
    /// A full BifrostQL host that mounts the Prometheus <c>/metrics</c> front door through the REAL
    /// <see cref="BifrostPrometheusExtensions.AddBifrostPrometheus"/> /
    /// <see cref="BifrostPrometheusExtensions.UseBifrostPrometheus"/> registration over a seeded
    /// in-memory SQLite endpoint — the integration counterpart of the Server-test e2e host, but
    /// parameterized so the scrape-load and intent-seam tests can vary the security options, the
    /// fixed service identity, and inject their own observers.
    ///
    /// <para>Every scrape runs the whole shipped pipeline: the credential gate, the scope resolver,
    /// the slice-2 collector, and — because a real BifrostQL endpoint is registered — the live
    /// tenant-filter / soft-delete / policy transformer chain on each aggregate's
    /// <see cref="IQueryIntentExecutor"/> read. Nothing is stubbed below the HTTP wire.</para>
    /// </summary>
    public sealed class PrometheusScrapeHost : IAsyncDisposable
    {
        public const string EndpointPath = "/graphql";
        public const string MetricsPath = "/metrics";
        public const string Credential = "scrape-secret";

        private readonly SqliteConnection _keepAlive;
        private readonly IHost _host;

        private PrometheusScrapeHost(SqliteConnection keepAlive, IHost host)
        {
            _keepAlive = keepAlive;
            _host = host;
        }

        /// <summary>
        /// Builds a scrape host. <paramref name="serviceIdentity"/> is the fixed principal a
        /// tenant-scoped (aggregate-mode) metric's aggregate runs under; <paramref name="configure"/>
        /// lets a test override the exposition/collection knobs (cache TTL, cardinality backstop,
        /// query timeout); <paramref name="observers"/> are additional query observers composed into
        /// the live read pipeline (used to count/capture the aggregate queries a scrape issues).
        /// </summary>
        public static async Task<PrometheusScrapeHost> StartAsync(
            string name,
            string[] metadata,
            string[] seed,
            bool armed = true,
            ClaimsPrincipal? serviceIdentity = null,
            Action<PrometheusServerOptions>? configure = null,
            IEnumerable<IQueryObserver>? observers = null)
        {
            var connString = $"Data Source=prom_it_{name}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var keepAlive = new SqliteConnection(connString);
            await keepAlive.OpenAsync();
            foreach (var sql in seed)
            {
                await using var cmd = keepAlive.CreateCommand();
                cmd.CommandText = sql;
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
                            e.ConnectionString = connString;
                            e.Provider = "sqlite";
                            e.Path = EndpointPath;
                            e.Metadata = metadata;
                            e.DisableAuth = true;
                        });
                    });

                    if (observers is not null)
                        foreach (var observer in observers)
                            services.AddSingleton(observer);

                    services.AddBifrostPrometheus(p =>
                    {
                        p.Security = new PrometheusScrapeSecurityOptions
                        {
                            BusinessMetricsEnabled = armed,
                            ScrapeCredential = armed ? Credential : null,
                            ServiceIdentity = serviceIdentity,
                        };
                        p.Exposition = new PrometheusExpositionOptions { Endpoint = EndpointPath };
                        configure?.Invoke(p);
                    });
                });
                web.Configure(app =>
                {
                    app.UseBifrostPrometheus();
                    app.UseBifrostEndpoints();
                });
            });

            var host = await builder.StartAsync();
            return new PrometheusScrapeHost(keepAlive, host);
        }

        public HttpClient Client => _host.GetTestClient();

        public IQueryIntentExecutor Reads => _host.Services.GetRequiredService<IQueryIntentExecutor>();

        /// <summary>A GET <c>/metrics</c> request carrying the bearer credential (or none).</summary>
        public HttpRequestMessage Scrape(string? credential = Credential)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, MetricsPath);
            if (credential is not null)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            return req;
        }

        public async Task<string> ScrapeBodyAsync(string? credential = Credential)
        {
            using var response = await Client.SendAsync(Scrape(credential));
            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>
        /// A fixed service principal projecting <c>tenant_id</c> (and roles) through the shared
        /// <see cref="IBifrostAuthContextFactory"/> — the scoping authority for an aggregate-mode
        /// tenant metric. A subject claim is required or the auth factory fails closed.
        /// </summary>
        public static ClaimsPrincipal ServicePrincipal(string tenantId, params string[] roles)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "prometheus-service"),
                new(LocalAuthClaims.Tenant, tenantId),
            };
            foreach (var role in roles)
                claims.Add(new Claim(ClaimTypes.Role, role));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            await _keepAlive.DisposeAsync();
        }
    }
}
