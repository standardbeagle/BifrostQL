using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Integration.Test.Prometheus
{
    /// <summary>
    /// Criterion 3 — the exposition output is verified against the Prometheus <c>0.0.4</c> text
    /// grammar, and a pinned Grafana/Prometheus example config is kept consistent with the real
    /// metric names WITHOUT making a live external Grafana/Prometheus a normal test dependency.
    ///
    /// <para>HONESTY NOTE: no Prometheus / prometheus-net text-parse library is available as a test
    /// dependency in this repo, so the exposition output is NOT validated by a third-party library.
    /// It is validated by <see cref="PrometheusTextFormatParser"/>, a from-scratch, spec-conformant
    /// 0.0.4 grammar check (metric/label name grammar, TYPE/HELP ordering + uniqueness, quoted/escaped
    /// label values, Go-float sample values, histogram <c>le</c> buckets). The Grafana dashboard and
    /// Prometheus scrape config are committed fixtures / doc examples; nothing here scrapes a live
    /// service.</para>
    /// </summary>
    public sealed class PrometheusExpositionConformanceTests
    {
        // A metadata-marked business metric with a label (so the exposition carries a labeled series),
        // plus a tenant table driving engine self-metrics through the live read path.
        private static readonly string[] Metadata =
        {
            "main.Sales { metric-name: sales_total; metric-help: Sales rows; metric-count: enabled; metric-sum: amount; metric-labels: region }",
        };

        private static readonly string[] Seed =
        {
            "CREATE TABLE Sales (id INTEGER PRIMARY KEY, region TEXT NOT NULL, amount REAL NOT NULL);",
            "INSERT INTO Sales(id, region, amount) VALUES (1, 'west', 10.0), (2, 'east', 2.0), (3, 'west', 3.0);",
        };

        // Drives one real (non-scrape) read through the endpoint's live pipeline so the DI-registered
        // engine self-metric registry records a read + its SQL-duration histogram — the same registry
        // the scrape renders. (The scrape's own aggregate queries are marked scrape-internal and
        // excluded, so the engine families only appear once genuine traffic has flowed.)
        private static async Task ExerciseEngineAsync(PrometheusScrapeHost host)
        {
            var model = await host.Reads.GetModelAsync(PrometheusScrapeHost.EndpointPath);
            var table = model.GetTableFromDbName("Sales");
            await host.Reads.ExecuteAsync(new QueryIntent
            {
                Query = new GqlObjectQuery
                {
                    DbTable = table,
                    SchemaName = table.TableSchema,
                    TableName = table.DbName,
                    GraphQlName = table.GraphQlName,
                    Path = table.GraphQlName,
                    ScalarColumns = { new GqlObjectColumn("id"), new GqlObjectColumn("region") },
                },
                UserContext = new Dictionary<string, object?>(),
                Endpoint = PrometheusScrapeHost.EndpointPath,
            });
        }

        [Fact]
        public async Task Exposition_output_parses_under_the_0_0_4_grammar_with_the_expected_families()
        {
            await using var host = await PrometheusScrapeHost.StartAsync("conformance", Metadata, Seed);
            await ExerciseEngineAsync(host); // populate the engine counter + histogram families

            var body = await host.ScrapeBodyAsync();

            // The whole body must validate under the 0.0.4 grammar (throws on any violation).
            var parsed = PrometheusTextFormatParser.Parse(body);

            // The business metric and its labeled series are present and correctly typed.
            parsed.HasMetric("sales_total").Should().BeTrue();
            parsed.Series("sales_total", ("region", "west")).Value.Should().Be(2);
            parsed.Types.Should().ContainKey("sales_total").WhoseValue.Should().Be("gauge");
            // The content-type version the endpoint advertises is the 0.0.4 text format.
            using var response = await host.Client.SendAsync(host.Scrape());
            response.Content.Headers.ContentType!.ToString().Should().Contain("version=0.0.4");
        }

        [Fact]
        public async Task Engine_histogram_output_parses_under_the_histogram_grammar()
        {
            await using var host = await PrometheusScrapeHost.StartAsync("conformance-hist", Metadata, Seed);
            await ExerciseEngineAsync(host);

            var parsed = PrometheusTextFormatParser.Parse(await host.ScrapeBodyAsync());

            // The engine SQL-duration histogram declares the histogram type and emits _bucket (with le),
            // _sum, and _count siblings — all of which the grammar check accepts under the base type.
            parsed.Types.Should().ContainKey("bifrostql_engine_sql_duration_seconds")
                .WhoseValue.Should().Be("histogram");
            parsed.HasMetric("bifrostql_engine_sql_duration_seconds_bucket").Should().BeTrue();
            parsed.HasMetric("bifrostql_engine_sql_duration_seconds_count").Should().BeTrue();
        }

        [Fact]
        public void The_parser_rejects_malformed_exposition_text()
        {
            // A defensive proof the grammar check has teeth: an invalid metric name is rejected.
            var bad = "# TYPE 1bad gauge\n1bad 1\n";
            var act = () => PrometheusTextFormatParser.Parse(bad);
            act.Should().Throw<System.FormatException>();
        }

        [Fact]
        public void The_pinned_grafana_and_prometheus_configs_reference_the_real_wire_contract()
        {
            var dir = FixturesDir();

            var scrapeConfig = File.ReadAllText(Path.Combine(dir, "prometheus-scrape-config.yml"));
            scrapeConfig.Should().Contain("metrics_path: /metrics");
            scrapeConfig.Should().Contain("Bearer"); // the scrape credential is presented as a bearer token

            var dashboardJson = File.ReadAllText(Path.Combine(dir, "grafana-dashboard.json"));
            using var doc = JsonDocument.Parse(dashboardJson); // must be valid JSON
            dashboardJson.Should().Contain("sales_total"); // charts the metadata-marked business metric
            dashboardJson.Should().Contain("bifrostql_prometheus_scrape_success"); // and a scrape-health metric
        }

        private static string FixturesDir([CallerFilePath] string thisFile = "") =>
            Path.Combine(Path.GetDirectoryName(thisFile)!, "fixtures");
    }
}
