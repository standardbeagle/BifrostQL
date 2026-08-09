using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Observers;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Auth;
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
    /// Security-conformance facts for the Prometheus scrape surface, equivalent to
    /// <c>ProtocolAdapterConformanceTests</c> — the fourth client-facing front door that never
    /// derived the kit, and the surface protocol-adapter-security INVARIANT 11 was written for.
    ///
    /// <para><b>Why this is not a derivation of the kit</b>: every kit fact is expressed as a
    /// request carrying a CALLER IDENTITY (<c>ConformanceReadRequest.Principal</c>) that the
    /// adapter must project and be scoped by. A Prometheus scrape has NO per-request caller at
    /// all — that absence is the entire hazard invariant 11 addresses — so
    /// <c>Read_TenantPrincipal_SeesOnlyItsOwnTenantRows</c>,
    /// <c>Read_WithoutTenantIdentity_FailsClosed</c> and the policy-denied-column facts have no
    /// per-caller counterpart here: the contract they encode is meaningless on this wire, not
    /// merely awkward. The surface is also read-only (no mutation verb exists), so the kit's five
    /// mutation facts are inapplicable in the same way <c>AdapterSupportsMutations = false</c>
    /// makes them for a read-only adapter.</para>
    ///
    /// <para><b>What IS asserted here</b>, on the real <c>/metrics</c> wire: the kit's
    /// transformers-apply and SQL-parameterized facts, re-expressed against the surface's ONE
    /// scoping authority (the configured service identity) instead of a per-request caller; plus
    /// invariant 11's own facts — opt-in and default OFF, credential AND mode both required, every
    /// misconfiguration failing closed to NO output rather than an unscoped global run, the cache
    /// key partitioning by security mode as well as identity, and self-metric labels being
    /// structurally bounded.</para>
    ///
    /// <para>The fixture spans TWO tenants deliberately. A single-tenant fixture cannot manifest
    /// the failure these facts exist to catch: an aggregate that silently spans every partition
    /// looks identical to a correctly scoped one.</para>
    /// </summary>
    public sealed class PrometheusScrapeConformanceTests
    {
        private const string Credential = "scrape-secret";
        private const string EndpointPath = "/graphql";
        private const string MetricsPath = "/metrics";

        private static readonly string[] Seed =
        {
            "CREATE TABLE Orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, status TEXT NOT NULL, amount REAL NOT NULL);",
            "INSERT INTO Orders(id, tenant_id, status, amount) VALUES " +
                "(1, 'tenant-a', 'open', 100.0), (2, 'tenant-a', 'open', 50.0), (3, 'tenant-a', 'closed', 25.0), " +
                "(4, 'tenant-b', 'open', 999.0), (5, 'tenant-b', 'closed', 888.0);",
        };

        // tenant-a holds 2 open rows, tenant-b holds 1. An unscoped aggregate reports 3 for
        // status="open" — which is exactly how a cross-tenant leak shows itself here.
        private const int TenantAOpenCount = 2;
        private const int GlobalOpenCount = 3;

        private const string AggregateMode =
            "main.Orders { tenant-filter: tenant_id; metric-name: orders_total; metric-count: enabled; " +
            "metric-sum: amount; metric-labels: status; metric-security-mode: aggregate }";
        private const string PerTenantWithoutTenantLabel =
            "main.Orders { tenant-filter: tenant_id; metric-name: orders_total; metric-count: enabled; " +
            "metric-sum: amount; metric-labels: status; metric-security-mode: per-tenant }";

        private static ClaimsPrincipal ServiceIdentity(string tenant) =>
            new(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "prometheus-service"),
                    new Claim(LocalAuthClaims.Tenant, tenant),
                },
                authenticationType: "test"));

        // ---- (a) the scoping authority actually scopes, and the SQL is parameterized ----------

        [Fact]
        public async Task An_armed_scrape_emits_only_the_service_identitys_partition()
        {
            await using var host = await ScrapeHost.StartAsync(
                "conf-scoped", AggregateMode, Seed, credential: Credential, serviceIdentity: ServiceIdentity("tenant-a"));

            var body = await host.ScrapeAsync(Credential);

            SampleValue(body, "orders_total", "open").Should().Be(TenantAOpenCount,
                "the aggregate runs under the configured service identity, so it reports that " +
                "partition only — not the {0}-row global total", GlobalOpenCount);
            body.Should().NotContain("999", "tenant-b's amounts must never reach the scrape wire");
            body.Should().NotContain("888");
        }

        [Fact]
        public async Task The_scrapes_aggregate_carries_the_tenant_predicate_and_binds_it_as_a_parameter()
        {
            await using var host = await ScrapeHost.StartAsync(
                "conf-sql", AggregateMode, Seed, credential: Credential, serviceIdentity: ServiceIdentity("tenant-a"));

            await host.ScrapeAsync(Credential);

            // The scrape service builds NO predicate of its own: the tenant WHERE is the
            // transformer pipeline's, injected because the aggregate crossed IQueryIntentExecutor.
            var sql = string.Join("\n---\n", host.Sql.SqlFor("Orders"));
            sql.Should().NotBeNullOrEmpty("the scrape's aggregate must reach SQL execution");
            sql.Should().MatchRegex(@"WHERE[\s\S]*tenant_id");
            sql.Should().NotContain("tenant-a", "the tenant value must bind as a parameter, never concatenate");
            sql.Should().Contain("@", "the WHERE clause must reference bound parameters");
        }

        // ---- (b) invariant 11: every misconfiguration fails closed to NO output ---------------

        [Fact]
        public async Task A_row_scoped_metric_with_no_service_identity_emits_no_series_at_all()
        {
            // Mode declared, credential configured, but no scoping authority. The failure mode this
            // guards is not "an error" — it is a silently UNSCOPED global aggregate.
            await using var host = await ScrapeHost.StartAsync(
                "conf-noidentity", AggregateMode, Seed, credential: Credential, serviceIdentity: null);

            var body = await host.ScrapeAsync(Credential);

            body.Should().NotContain("orders_total",
                "a row-scoped metric with no scoping authority is excluded, never run globally");
            body.Should().NotContain(GlobalOpenCount.ToString());
        }

        [Fact]
        public async Task Per_tenant_mode_without_the_tenant_label_emits_no_series_at_all()
        {
            // per-tenant mode partitions series BY the tenant label; a table that does not declare
            // it cannot be partitioned, so the series is dropped rather than blended.
            await using var host = await ScrapeHost.StartAsync(
                "conf-nolabel", PerTenantWithoutTenantLabel, Seed,
                credential: Credential, serviceIdentity: ServiceIdentity("tenant-a"));

            var body = await host.ScrapeAsync(Credential);

            body.Should().NotContain("orders_total");
        }

        [Fact]
        public async Task An_enabled_surface_with_no_credential_denies_every_scrape()
        {
            // Invariant 11 requires BOTH halves. Half-configured is not half-open: it is closed,
            // uniformly, whatever the scraper presents.
            await using var host = await ScrapeHost.StartAsync(
                "conf-halfarmed", AggregateMode, Seed,
                credential: null, serviceIdentity: ServiceIdentity("tenant-a"), businessMetricsEnabled: true);

            foreach (var presented in new[] { null, "", Credential })
            {
                using var response = await host.Client.SendAsync(Request(presented));
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                (await response.Content.ReadAsStringAsync()).Should().NotContain("orders_total");
            }
        }

        [Fact]
        public async Task Business_metrics_are_off_by_default_so_a_credentialed_scrape_still_denies()
        {
            await using var host = await ScrapeHost.StartAsync(
                "conf-default-off", AggregateMode, Seed,
                credential: Credential, serviceIdentity: ServiceIdentity("tenant-a"), businessMetricsEnabled: null);

            using var response = await host.Client.SendAsync(Request(Credential));

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "BusinessMetricsEnabled is left UNSET here, so this proves the DEFAULT rather than a " +
                "value the fixture supplied");
        }

        // ---- (c) the cache key partitions by security mode as well as identity ---------------

        [Fact]
        public async Task The_cache_key_partitions_by_security_mode()
        {
            // A key that omits the mode would serve a series collected under one security posture
            // to a scrape running under another after a config change — the same class of leak as
            // omitting the identity partition (which PrometheusScrapeServiceTests pins).
            await using var host = await ScrapeHost.StartAsync(
                "conf-key", AggregateMode, Seed, credential: Credential, serviceIdentity: ServiceIdentity("tenant-a"));

            var service = host.Services.GetRequiredService<PrometheusScrapeService>();
            var table = (await host.Reads.GetModelAsync(EndpointPath)).GetTableFromDbName("Orders");
            var aggregate = PrometheusMetricConfig.FromTable(table);
            var perTenant = PrometheusMetricConfig.FromTable(
                await TableWithMetadataAsync("conf-key-alt", PerTenantWithoutTenantLabel));
            IDictionary<string, object?> context = new Dictionary<string, object?> { ["tenant_id"] = "tenant-a" };

            service.CacheKey("model", aggregate, context)
                .Should().NotBe(service.CacheKey("model", perTenant, context));
        }

        // ---- (d) self/operational metric labels are structurally bounded ----------------------

        [Fact]
        public void Engine_self_metric_label_dimensions_are_unrepresentable_as_free_text()
        {
            // Convention is not enough: a tenant id, user id, table name, raw SQL or exception
            // string reaching a label is both a cardinality DoS and a disclosure channel. The
            // record API must make that IMPOSSIBLE, not merely discouraged — so no recording
            // parameter may be a string.
            var recordParameters = typeof(EngineMetrics)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.StartsWith("Record", StringComparison.Ordinal)
                    || m.Name.Contains("Connection", StringComparison.Ordinal))
                .SelectMany(m => m.GetParameters())
                .ToList();

            recordParameters.Should().NotBeEmpty("the registry must expose recording entry points");
            recordParameters.Should().OnlyContain(
                p => p.ParameterType.IsEnum || p.ParameterType.IsPrimitive,
                "every label dimension must be a finite enum; a string parameter would let an " +
                "unbounded value become a label");
        }

        // ---- helpers -------------------------------------------------------------------------

        private static HttpRequestMessage Request(string? credential)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, MetricsPath);
            if (credential is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            return request;
        }

        /// <summary>The value of one exposition sample, e.g. <c>orders_total{status="open"} 2</c>.</summary>
        private static double SampleValue(string body, string metric, string statusLabel)
        {
            var match = Regex.Match(
                body, $@"^{Regex.Escape(metric)}\{{status=""{statusLabel}""\}}\s+([0-9.eE+-]+)$",
                RegexOptions.Multiline);
            match.Success.Should().BeTrue($"the scrape body must carry a '{metric}' sample for status={statusLabel}:\n{body}");
            return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>A throwaway host used only to parse an alternative metric declaration.</summary>
        private static async Task<IDbTable> TableWithMetadataAsync(string name, string metadata)
        {
            await using var host = await ScrapeHost.StartAsync(
                name, metadata, Seed, credential: Credential, serviceIdentity: ServiceIdentity("tenant-a"));
            return (await host.Reads.GetModelAsync(EndpointPath)).GetTableFromDbName("Orders");
        }

        /// <summary>
        /// A full BifrostQL host with the REAL <c>AddBifrostPrometheus</c>/<c>UseBifrostPrometheus</c>
        /// registration, a configurable scrape credential and service identity, and a query observer
        /// capturing the SQL each scrape's aggregate actually executed.
        /// </summary>
        private sealed class ScrapeHost : IAsyncDisposable
        {
            private readonly string _connString;
            private SqliteConnection _keepAlive = null!;
            private IHost _host = null!;

            public SqlCaptureObserver Sql { get; } = new();

            private ScrapeHost(string name) =>
                _connString = $"Data Source=promconf_{name}_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

            /// <param name="businessMetricsEnabled">
            /// Null leaves the option UNSET so a fact can exercise the real default rather than a
            /// value the fixture supplied.
            /// </param>
            public static async Task<ScrapeHost> StartAsync(
                string name,
                string metadata,
                string[] seed,
                string? credential,
                ClaimsPrincipal? serviceIdentity,
                bool? businessMetricsEnabled = true)
            {
                var host = new ScrapeHost(name);
                await host.InitializeAsync(metadata, seed, credential, serviceIdentity, businessMetricsEnabled);
                return host;
            }

            private async Task InitializeAsync(
                string metadata,
                string[] seed,
                string? credential,
                ClaimsPrincipal? serviceIdentity,
                bool? businessMetricsEnabled)
            {
                _keepAlive = new SqliteConnection(_connString);
                await _keepAlive.OpenAsync();
                foreach (var sql in seed)
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
                                e.Metadata = new[] { metadata };
                                e.DisableAuth = true;
                            });
                            o.AddQueryObservers(new IQueryObserver[] { Sql });
                        });
                        services.AddBifrostPrometheus(p =>
                        {
                            p.Security = new PrometheusScrapeSecurityOptions
                            {
                                BusinessMetricsEnabled = businessMetricsEnabled ?? false,
                                ScrapeCredential = credential,
                                ServiceIdentity = serviceIdentity,
                            };
                            p.Exposition = new PrometheusExpositionOptions { Endpoint = EndpointPath };
                        });
                    });
                    web.Configure(app =>
                    {
                        app.UseBifrostPrometheus();
                        app.UseBifrostEndpoints();
                    });
                });
                _host = await builder.StartAsync();
            }

            public HttpClient Client => _host.GetTestClient();

            public IServiceProvider Services => _host.Services;

            public IQueryIntentExecutor Reads => _host.Services.GetRequiredService<IQueryIntentExecutor>();

            /// <summary>Performs one authorized scrape and returns the exposition body.</summary>
            public async Task<string> ScrapeAsync(string? credential)
            {
                using var response = await Client.SendAsync(Request(credential));
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                return await response.Content.ReadAsStringAsync();
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
        }

        /// <summary>Captures the SQL each table's reads generated, at the phase carrying SQL text.</summary>
        internal sealed class SqlCaptureObserver : IQueryObserver
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
