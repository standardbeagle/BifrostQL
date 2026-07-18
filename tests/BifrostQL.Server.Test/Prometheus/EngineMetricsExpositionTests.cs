using System.Linq;
using BifrostQL.Core.Observers;
using BifrostQL.Server.Prometheus;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Prometheus
{
    /// <summary>
    /// Slice 5 criteria 2 and 4 for the engine-metric EXPOSITION: metric names carry the
    /// <c>bifrostql_</c> prefix, counters end in <c>_total</c>, duration histograms end in
    /// <c>_seconds</c> with stable buckets/units, and — the anti-cardinality/anti-leak crux — ONLY
    /// the bounded enum labels (operation/outcome/adapter) ever reach the wire; no table, tenant,
    /// user, or exception text can, because the snapshot cannot carry one.
    /// </summary>
    public sealed class EngineMetricsExpositionTests
    {
        private static string Render(EngineMetrics metrics) =>
            EngineMetricsExposition.Render(metrics.Snapshot());

        [Fact]
        public void Counter_name_prefix_total_suffix_and_bounded_labels()
        {
            var metrics = new EngineMetrics(enabled: true);
            metrics.RecordRequest(EngineOperation.Read, EngineRequestOutcome.Success);
            metrics.RecordRequest(EngineOperation.Write, EngineRequestOutcome.Error);

            var text = Render(metrics);

            text.Should().Contain("# TYPE bifrostql_engine_requests_total counter");
            text.Should().Contain("bifrostql_engine_requests_total{operation=\"read\",outcome=\"success\"} 1");
            text.Should().Contain("bifrostql_engine_requests_total{operation=\"write\",outcome=\"error\"} 1");
        }

        [Fact]
        public void Sql_duration_is_a_seconds_histogram_with_stable_buckets_sum_count()
        {
            var metrics = new EngineMetrics(enabled: true);
            metrics.RecordSqlDuration(EngineOperation.Read, 0.02);

            var text = Render(metrics);

            text.Should().Contain("# TYPE bifrostql_engine_sql_duration_seconds histogram");
            // every configured boundary is present as an le bucket, plus +Inf
            foreach (var le in EngineMetrics.DurationBucketsSeconds)
                text.Should().Contain($"bifrostql_engine_sql_duration_seconds_bucket{{operation=\"read\",le=\"{le.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"}}");
            text.Should().Contain("bifrostql_engine_sql_duration_seconds_bucket{operation=\"read\",le=\"+Inf\"} 1");
            text.Should().Contain("bifrostql_engine_sql_duration_seconds_sum{operation=\"read\"} 0.02");
            text.Should().Contain("bifrostql_engine_sql_duration_seconds_count{operation=\"read\"} 1");
        }

        [Fact]
        public void Transformer_duration_uses_the_seconds_histogram_naming()
        {
            var metrics = new EngineMetrics(enabled: true);
            metrics.RecordTransformerDuration(EngineOperation.Write, 0.003);

            var text = Render(metrics);

            text.Should().Contain("# TYPE bifrostql_engine_transformer_duration_seconds histogram");
            text.Should().Contain("bifrostql_engine_transformer_duration_seconds_count{operation=\"write\"} 1");
        }

        [Fact]
        public void Active_connections_gauge_labeled_only_by_adapter_enum()
        {
            var metrics = new EngineMetrics(enabled: true);
            metrics.ConnectionOpened(EngineAdapter.Grpc);

            var text = Render(metrics);

            text.Should().Contain("# TYPE bifrostql_engine_active_connections gauge");
            text.Should().Contain("bifrostql_engine_active_connections{adapter=\"grpc\"} 1");
        }

        [Fact]
        public void Labels_are_bounded_enums_never_table_tenant_or_exception_text()
        {
            // Even a torrent of records over EVERY enum value can only ever produce the fixed set of
            // label values below — the record API takes no free-form string, so an unbounded/PII label
            // is structurally impossible. Prove the rendered wire carries ONLY the finite domain.
            var metrics = new EngineMetrics(enabled: true);
            foreach (EngineOperation op in System.Enum.GetValues(typeof(EngineOperation)))
                foreach (EngineRequestOutcome oc in System.Enum.GetValues(typeof(EngineRequestOutcome)))
                    metrics.RecordRequest(op, oc);
            foreach (EngineAdapter a in System.Enum.GetValues(typeof(EngineAdapter)))
                metrics.ConnectionOpened(a);
            metrics.RecordSqlDuration(EngineOperation.Read, 0.02);

            var text = Render(metrics);

            // No unbounded/PII dimension can appear as a label key.
            text.Should().NotContain("table=");
            text.Should().NotContain("tenant=");
            text.Should().NotContain("user=");
            text.Should().NotContain("sql=");
            text.Should().NotContain("exception=");
            text.Should().NotContain("error_message");

            // Only the finite label VALUE domain appears.
            var operationValues = System.Text.RegularExpressions.Regex
                .Matches(text, "operation=\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value).Distinct();
            operationValues.Should().BeSubsetOf(new[] { "read", "write" });

            var adapterValues = System.Text.RegularExpressions.Regex
                .Matches(text, "adapter=\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value).Distinct();
            adapterValues.Should().BeSubsetOf(new[]
                { "graphql", "odata", "grpc", "prometheus", "pgwire", "resp", "mcp", "s3", "other" });

            var outcomeValues = System.Text.RegularExpressions.Regex
                .Matches(text, "outcome=\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value).Distinct();
            outcomeValues.Should().BeSubsetOf(new[] { "success", "error", "denied" });
        }

        [Fact]
        public void Idle_registry_renders_nothing()
        {
            var metrics = new EngineMetrics(enabled: true);
            Render(metrics).Should().BeEmpty();
        }
    }
}
