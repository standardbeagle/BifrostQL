using System.Collections.Generic;
using System.Linq;
using BifrostQL.Server.Prometheus;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Prometheus
{
    /// <summary>
    /// Criterion 1: the 0.0.4 exposition writer emits valid HELP/TYPE lines, applies Prometheus
    /// escaping to HELP text and label values, and renders families/samples in a deterministic,
    /// byte-stable order regardless of input order.
    /// </summary>
    public sealed class PrometheusExpositionWriterTests
    {
        private static PrometheusExpositionSample Sample(double value, params (string k, string v)[] labels) =>
            new(labels.Select(l => new KeyValuePair<string, string>(l.k, l.v)).ToList(), value);

        [Fact]
        public void Emits_help_type_and_sample_lines()
        {
            var family = new PrometheusExpositionFamily(
                "orders_total", "Orders placed", PrometheusExpositionWriter.GaugeType,
                new[] { Sample(3, ("status", "open")) });

            var text = PrometheusExpositionWriter.Write(new[] { family });

            text.Should().Be(
                "# HELP orders_total Orders placed\n" +
                "# TYPE orders_total gauge\n" +
                "orders_total{status=\"open\"} 3\n");
        }

        [Fact]
        public void Escapes_help_backslash_and_newline()
        {
            var family = new PrometheusExpositionFamily(
                "m", "line1\nline2 C:\\path", PrometheusExpositionWriter.GaugeType,
                new[] { Sample(1) });

            var text = PrometheusExpositionWriter.Write(new[] { family });

            text.Should().Contain("# HELP m line1\\nline2 C:\\\\path\n");
        }

        [Fact]
        public void Escapes_label_value_quote_backslash_and_newline()
        {
            var family = new PrometheusExpositionFamily(
                "m", null, PrometheusExpositionWriter.GaugeType,
                new[] { Sample(1, ("l", "a\"b\\c\nd")) });

            var text = PrometheusExpositionWriter.Write(new[] { family });

            // " -> \" , \ -> \\ , newline -> \n
            text.Should().Contain("m{l=\"a\\\"b\\\\c\\nd\"} 1\n");
        }

        [Fact]
        public void Omits_help_when_absent_but_still_emits_type()
        {
            var family = new PrometheusExpositionFamily(
                "m", null, PrometheusExpositionWriter.GaugeType, new[] { Sample(1) });

            var text = PrometheusExpositionWriter.Write(new[] { family });

            text.Should().Be("# TYPE m gauge\nm 1\n");
        }

        [Fact]
        public void Orders_families_by_name_and_samples_by_label_block_deterministically()
        {
            // Families supplied out of order; samples supplied out of order.
            var zebra = new PrometheusExpositionFamily(
                "zebra", null, PrometheusExpositionWriter.GaugeType,
                new[] { Sample(1, ("k", "west")), Sample(2, ("k", "east")) });
            var alpha = new PrometheusExpositionFamily(
                "alpha", null, PrometheusExpositionWriter.GaugeType,
                new[] { Sample(9, ("k", "b")), Sample(8, ("k", "a")) });

            var text = PrometheusExpositionWriter.Write(new[] { zebra, alpha });

            var lines = text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
            lines.Should().Equal(
                "# TYPE alpha gauge",
                "alpha{k=\"a\"} 8",
                "alpha{k=\"b\"} 9",
                "# TYPE zebra gauge",
                "zebra{k=\"east\"} 2",
                "zebra{k=\"west\"} 1");
        }

        [Fact]
        public void Empty_families_are_skipped()
        {
            var family = new PrometheusExpositionFamily(
                "m", "h", PrometheusExpositionWriter.GaugeType, System.Array.Empty<PrometheusExpositionSample>());

            PrometheusExpositionWriter.Write(new[] { family }).Should().BeEmpty();
        }
    }
}
