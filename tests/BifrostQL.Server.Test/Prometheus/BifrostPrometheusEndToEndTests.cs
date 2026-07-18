using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Prometheus;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test.Prometheus
{
    /// <summary>
    /// End-to-end wiring of the Prometheus <c>/metrics</c> front door through the REAL
    /// <see cref="BifrostPrometheusExtensions.AddBifrostPrometheus"/> +
    /// <see cref="BifrostPrometheusExtensions.UseBifrostPrometheus"/> host registration (mirroring the
    /// gRPC/OData e2e stacks). Proves the whole scrape works end-to-end against a seeded in-memory
    /// SQLite endpoint AND that the engine self-metrics registered by <c>AddBifrostPrometheus</c> are
    /// live-wired into the real <see cref="SqlExecutionManager"/> read path (so error/denied outcomes
    /// and transformer-pipeline duration actually increment), while the fail-closed posture is
    /// preserved: business metrics default OFF, the credential gate is first, and denial is uniform
    /// (absent ≡ wrong ≡ disabled).
    /// </summary>
    public sealed class BifrostPrometheusEndToEndTests
    {
        private const string Credential = "scrape-secret";
        private const string EndpointPath = "/graphql";

        // Sales declares a business metric (scraped internally); Orders carries a tenant-filter so a
        // read WITH a claim succeeds (records transformer duration + success) and a read WITHOUT one
        // fails closed (records a denied outcome) — both on the live SqlExecutionManager intent path.
        private static readonly string[] Metadata =
        {
            "main.Sales { metric-name: sales_total; metric-count: enabled; metric-sum: amount }",
            "main.Orders { tenant-filter: tenant_id }",
        };

        private static readonly string[] Seed =
        {
            "CREATE TABLE Sales (id INTEGER PRIMARY KEY, region TEXT NOT NULL, amount REAL NOT NULL);",
            "INSERT INTO Sales(id, region, amount) VALUES (1, 'west', 10.0), (2, 'east', 2.0);",
            "CREATE TABLE Orders (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, name TEXT NOT NULL);",
            "INSERT INTO Orders(id, tenant_id, name) VALUES (1, 1, 'a'), (2, 2, 'b');",
        };

        private static HttpRequestMessage Scrape(string? credential)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, PrometheusHost.MetricsPath);
            if (credential is not null)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            return req;
        }

        private static GqlObjectQuery OrdersSelect(IDbModel model)
        {
            var table = model.GetTableFromDbName("Orders");
            return new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
                ScalarColumns =
                {
                    new GqlObjectColumn("id"),
                    new GqlObjectColumn("tenant_id"),
                    new GqlObjectColumn("name"),
                },
            };
        }

        // Drive one successful and one denied read across the real SqlExecutionManager intent path so
        // the engine registry (shared with the scrape service) has non-zero success/denied/transformer
        // observations before we scrape.
        private static async Task ExerciseEngineAsync(PrometheusHost host)
        {
            var reads = host.Reads;
            var model = await reads.GetModelAsync(EndpointPath);

            // Success: a tenant claim satisfies the filter → transformer duration + read-success.
            await reads.ExecuteAsync(new QueryIntent
            {
                Query = OrdersSelect(model),
                UserContext = new Dictionary<string, object?> { ["tenant_id"] = 1 },
                Endpoint = EndpointPath,
            });

            // Denied: no tenant claim → the tenant filter throws AccessDenied → read-denied outcome.
            var denied = () => reads.ExecuteAsync(new QueryIntent
            {
                Query = OrdersSelect(model),
                UserContext = new Dictionary<string, object?>(),
                Endpoint = EndpointPath,
            });
            await denied.Should().ThrowAsync<BifrostExecutionError>();
        }

        // ---- (i) armed scrape → 200 with business + engine + health series -----------------------

        [Fact]
        public async Task An_armed_scrape_returns_business_engine_and_health_series()
        {
            await using var host = await PrometheusHost.StartAsync("e2e-armed", Metadata, Seed, armed: true);
            await ExerciseEngineAsync(host);

            using var response = await host.Client.SendAsync(Scrape(Credential));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();

            // Business series (from the DB-derived Sales metric).
            body.Should().Contain("sales_total");
            // Health series (scrape posture, independent of business-series success).
            body.Should().Contain(PrometheusScrapeService.ScrapeSuccessMetric);
            // Engine self-metrics, live-wired through SqlExecutionManager: both the observer-recorded
            // success and the manager-recorded denial reached the shared registry.
            body.Should().Contain(EngineMetricsExposition.RequestsMetric);
            body.Should().Contain("operation=\"read\",outcome=\"success\"");
            body.Should().Contain("operation=\"read\",outcome=\"denied\"");
            // Transformer-pipeline duration recorded directly on the read path (not an observer phase).
            body.Should().Contain(EngineMetricsExposition.TransformerDurationMetric);
        }

        // ---- (ii) unconfigured / disabled host serves NO business metrics (fail-closed) ----------

        [Fact]
        public async Task A_disabled_host_serves_no_business_metrics_and_denies()
        {
            // Registered but disarmed: business metrics OFF, no credential → the gate denies every scrape.
            await using var host = await PrometheusHost.StartAsync("e2e-disabled", Metadata, Seed, armed: false);

            using var response = await host.Client.SendAsync(Scrape(Credential));

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("sales_total");
        }

        [Fact]
        public async Task An_unconfigured_host_does_not_mount_the_metrics_endpoint()
        {
            // AddBifrostPrometheus never called; UseBifrostPrometheus is inert → no /metrics route at all.
            await using var host = await PrometheusHost.StartAsync(
                "e2e-unconfigured", Metadata, Seed, armed: false, registerPrometheus: false);

            using var response = await host.Client.SendAsync(Scrape(Credential));

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        // ---- (iii) uniform denial: absent ≡ wrong ≡ disabled -------------------------------------

        [Fact]
        public async Task Absent_wrong_and_disabled_credentials_all_return_the_identical_denial()
        {
            await using var armed = await PrometheusHost.StartAsync("e2e-uniform-on", Metadata, Seed, armed: true);
            await using var disabled = await PrometheusHost.StartAsync("e2e-uniform-off", Metadata, Seed, armed: false);

            using var absent = await armed.Client.SendAsync(Scrape(null));
            using var wrong = await armed.Client.SendAsync(Scrape("nope"));
            using var offWithSecret = await disabled.Client.SendAsync(Scrape(Credential));

            foreach (var response in new[] { absent, wrong, offWithSecret })
            {
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
                (await response.Content.ReadAsStringAsync()).Should().Be("# unauthorized\n");
            }
        }

        /// <summary>
        /// A full BifrostQL host (seeded in-memory SQLite endpoint) wired through the real
        /// <c>AddBifrostPrometheus</c>/<c>UseBifrostPrometheus</c> registration, exposing the test HTTP
        /// client and the endpoint's live <see cref="IQueryIntentExecutor"/> (whose SqlExecutionManager
        /// resolves the same engine registry the scrape reads).
        /// </summary>
        private sealed class PrometheusHost : IAsyncDisposable
        {
            public const string MetricsPath = "/metrics";

            private readonly string _connString;
            private readonly string[] _metadata;
            private readonly string[] _seed;
            private readonly bool _armed;
            private readonly bool _registerPrometheus;
            private SqliteConnection _keepAlive = null!;
            private IHost _host = null!;

            private PrometheusHost(
                string connName, string[] metadata, string[] seed, bool armed, bool registerPrometheus)
            {
                _connString = $"Data Source=prom_{connName}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
                _metadata = metadata;
                _seed = seed;
                _armed = armed;
                _registerPrometheus = registerPrometheus;
            }

            public static async Task<PrometheusHost> StartAsync(
                string connName, string[] metadata, string[] seed, bool armed, bool registerPrometheus = true)
            {
                var host = new PrometheusHost(connName, metadata, seed, armed, registerPrometheus);
                await host.InitializeAsync();
                return host;
            }

            private async Task InitializeAsync()
            {
                _keepAlive = new SqliteConnection(_connString);
                await _keepAlive.OpenAsync();
                foreach (var sql in _seed)
                {
                    await using var cmd = _keepAlive.CreateCommand();
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
                                e.ConnectionString = _connString;
                                e.Provider = "sqlite";
                                e.Path = EndpointPath;
                                e.Metadata = _metadata;
                                e.DisableAuth = true;
                            });
                        });
                        if (_registerPrometheus)
                        {
                            services.AddBifrostPrometheus(p =>
                            {
                                p.Security = new PrometheusScrapeSecurityOptions
                                {
                                    BusinessMetricsEnabled = _armed,
                                    ScrapeCredential = _armed ? Credential : null,
                                };
                                p.Exposition = new PrometheusExpositionOptions { Endpoint = EndpointPath };
                            });
                        }
                    });
                    // UseBifrostPrometheus is called unconditionally — inert when not registered.
                    web.Configure(app =>
                    {
                        app.UseBifrostPrometheus();
                        app.UseBifrostEndpoints();
                    });
                });
                _host = await builder.StartAsync();
            }

            public HttpClient Client => _host.GetTestClient();

            public IQueryIntentExecutor Reads => _host.Services.GetRequiredService<IQueryIntentExecutor>();

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
        }
    }
}
