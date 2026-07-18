using System;
using System.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for Prometheus slice 1 — the metadata contract parsed from the
/// <c>metric-*</c> table metadata into a typed <see cref="PrometheusMetricConfig"/>.
/// This is the STRUCTURAL parse (name grammar, present-but-empty guards, count/sum
/// "at least one source" rule, security-mode/cardinality tokens, intra-metric label
/// normalization collisions); model-level checks (column existence, numeric type,
/// encrypted labels, tenant mode, cross-table duplicate series) are pinned in
/// <c>PrometheusMetricValidationTests</c>.
/// </summary>
public class PrometheusMetricConfigTests
{
    private static IDbTable Table(Action<DbModelTestFixture.TableBuilder> configure)
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t =>
            {
                t.WithSchema("dbo").WithPrimaryKey("Id").WithColumn("Total", "decimal").WithColumn("Status", "nvarchar");
                configure(t);
            })
            .Build();
        return model.Tables.Single();
    }

    [Fact]
    public void FromTable_NoMetricName_ReturnsNone()
    {
        var table = Table(_ => { });

        var config = PrometheusMetricConfig.FromTable(table);

        config.DeclaresMetric.Should().BeFalse();
        config.Should().BeSameAs(PrometheusMetricConfig.None);
    }

    [Fact]
    public void FromTable_HappyPath_ParsesEveryField()
    {
        var table = Table(t => t
            .WithMetadata(MetadataKeys.Metrics.Name, "orders_total")
            .WithMetadata(MetadataKeys.Metrics.Help, "Orders placed")
            .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll)
            .WithMetadata(MetadataKeys.Metrics.Sum, "Total")
            .WithMetadata(MetadataKeys.Metrics.Labels, "Status")
            .WithMetadata(MetadataKeys.Metrics.MaxCardinality, "50")
            .WithMetadata(MetadataKeys.Metrics.SecurityMode, MetadataKeys.Metrics.SecurityModePerTenant));

        var config = PrometheusMetricConfig.FromTable(table);

        config.DeclaresMetric.Should().BeTrue();
        config.MetricName.Should().Be("orders_total");
        config.ExportedName.Should().Be("orders_total");
        config.Help.Should().Be("Orders placed");
        config.CountsAllRows.Should().BeTrue();
        config.HasCount.Should().BeTrue();
        config.SumColumn.Should().Be("Total"); // canonicalized to DB casing
        config.Labels.Should().ContainSingle().Which.Should().Be("Status");
        config.MaxCardinality.Should().Be(50);
        config.SecurityMode.Should().Be(MetadataKeys.Metrics.SecurityModePerTenant);
    }

    [Fact]
    public void FromTable_EmptyName_Throws()
    {
        var table = Table(t => t.WithMetadata(MetadataKeys.Metrics.Name, "   "));

        var act = () => PrometheusMetricConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("empty");
    }

    [Theory]
    [InlineData("orders total")]   // space
    [InlineData("1orders")]        // leading digit
    [InlineData("orders-total")]   // dash
    public void FromTable_InvalidPrometheusName_Throws(string name)
    {
        var table = Table(t => t
            .WithMetadata(MetadataKeys.Metrics.Name, name)
            .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll));

        var act = () => PrometheusMetricConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("not a valid Prometheus metric name");
    }

    [Fact]
    public void FromTable_NeitherCountNorSum_Throws()
    {
        var table = Table(t => t.WithMetadata(MetadataKeys.Metrics.Name, "orders_total"));

        var act = () => PrometheusMetricConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Metrics.Count)
            .And.Contain(MetadataKeys.Metrics.Sum);
    }

    [Fact]
    public void FromTable_EmptySum_Throws()
    {
        var table = Table(t => t
            .WithMetadata(MetadataKeys.Metrics.Name, "orders_total")
            .WithMetadata(MetadataKeys.Metrics.Sum, ""));

        var act = () => PrometheusMetricConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(MetadataKeys.Metrics.Sum);
    }

    [Fact]
    public void FromTable_NonPositiveCardinality_Throws()
    {
        var table = Table(t => t
            .WithMetadata(MetadataKeys.Metrics.Name, "orders_total")
            .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll)
            .WithMetadata(MetadataKeys.Metrics.MaxCardinality, "0"));

        var act = () => PrometheusMetricConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Metrics.MaxCardinality);
    }

    [Fact]
    public void FromTable_UnknownSecurityMode_Throws()
    {
        var table = Table(t => t
            .WithMetadata(MetadataKeys.Metrics.Name, "orders_total")
            .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll)
            .WithMetadata(MetadataKeys.Metrics.SecurityMode, "bogus"));

        var act = () => PrometheusMetricConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Metrics.SecurityMode);
    }

    [Fact]
    public void FromTable_LabelsNormalizingToSameName_ThrowsCollision()
    {
        // Two distinct declared labels that normalize (lowercase) to the same exported
        // label name are a duplicate-series collision, not a silent overwrite.
        var table = Table(t => t
            .WithMetadata(MetadataKeys.Metrics.Name, "orders_total")
            .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll)
            .WithMetadata(MetadataKeys.Metrics.Labels, "Status,STATUS"));

        var act = () => PrometheusMetricConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("normalizes to the same exported label name");
    }

    [Fact]
    public void FromTable_ExportedNameNormalizesDeterministically()
    {
        var table = Table(t => t
            .WithMetadata(MetadataKeys.Metrics.Name, "Orders_Total")
            .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll));

        var config = PrometheusMetricConfig.FromTable(table);

        // Deterministic normalization: mixed case declared name → lower-cased exported name.
        config.ExportedName.Should().Be("orders_total");
    }
}
