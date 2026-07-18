using System;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Fail-fast validation coverage for the Prometheus business-metric metadata contract
/// (slice 1). A metric is exported over an operational wire, so a misconfiguration must
/// be caught at model load, not on the first scrape: an invalid metric name, a missing
/// or non-numeric sum source, an unknown label column, a duplicate series, an encrypted
/// label column, and a tenant-scoped table declaring a metric without an explicit
/// scrape-security mode are each rejected. Nothing is exported absent the opt-in.
/// </summary>
public class PrometheusMetricValidationTests
{
    [Fact]
    public void Validate_ValidMetric_DoesNotThrow()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithColumn("Status", "nvarchar")
                .WithMetadata(MetadataKeys.Metrics.Name, "orders_total")
                .WithMetadata(MetadataKeys.Metrics.Help, "Orders placed")
                .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll)
                .WithMetadata(MetadataKeys.Metrics.Sum, "Total")
                .WithMetadata(MetadataKeys.Metrics.Labels, "Status"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_NoMetricConfigured_DoesNotThrow()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_InvalidMetricName_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Metrics.Name, "orders-total") // dash: invalid
                .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("not a valid Prometheus metric name");
    }

    [Fact]
    public void Validate_SumSourceMissingColumn_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.Metrics.Name, "orders_total")
                .WithMetadata(MetadataKeys.Metrics.Sum, "Total")) // no such column
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Metrics.Sum).And.Contain("does not exist");
    }

    [Fact]
    public void Validate_SumSourceNonNumeric_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithMetadata(MetadataKeys.Metrics.Name, "orders_total")
                .WithMetadata(MetadataKeys.Metrics.Sum, "Status")) // string: non-numeric
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("must be numeric");
    }

    [Fact]
    public void Validate_UnknownLabelColumn_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Metrics.Name, "orders_total")
                .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll)
                .WithMetadata(MetadataKeys.Metrics.Labels, "Ghost")) // no such column
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Metrics.Labels).And.Contain("does not exist");
    }

    [Fact]
    public void Validate_EncryptedLabelColumn_Throws()
    {
        // A field-encrypted column may not be a metric label: labels are cleartext
        // exposition, so it would publish the plaintext the encryption protects.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithColumn("Ssn", "nvarchar")
                .WithColumnMetadata("Ssn", MetadataKeys.Crypto.Encrypt, MetadataKeys.Crypto.AlgorithmAes256Gcm)
                .WithColumnMetadata("Ssn", MetadataKeys.Crypto.KeyRef, "kms:pii")
                .WithMetadata(MetadataKeys.Metrics.Name, "orders_total")
                .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll)
                .WithMetadata(MetadataKeys.Metrics.Labels, "Ssn"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("field-encrypted").And.Contain("Ssn");
    }

    [Fact]
    public void Validate_DuplicateSeries_Throws()
    {
        // Two tables export the same metric name with the same (empty) label set:
        // a duplicate series that would collide on the exposition wire.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.Metrics.Name, "rows_total")
                .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll))
            .WithTable("widgets", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.Metrics.Name, "rows_total")
                .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("same metric series");
    }

    [Fact]
    public void Validate_NormalizationCollisionAcrossTables_ThrowsDuplicateSeries()
    {
        // Two DISTINCT declared names ('rows_total' / 'Rows_Total') normalize to the same
        // exported name — a duplicate series, not a silent overwrite (criterion 3).
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.Metrics.Name, "rows_total")
                .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll))
            .WithTable("widgets", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.Metrics.Name, "Rows_Total")
                .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("same metric series");
    }

    [Fact]
    public void Validate_TenantScopedMetricWithoutMode_Throws()
    {
        // A tenant-filtered table cannot declare a metric without explicitly choosing a
        // scrape-security mode, or it would export an ambient cross-tenant aggregate.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("TenantId", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "TenantId")
                .WithMetadata(MetadataKeys.Metrics.Name, "orders_total")
                .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Metrics.SecurityMode);
    }

    [Fact]
    public void Validate_TenantScopedMetricWithExplicitMode_DoesNotThrow()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("TenantId", "int")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Security.TenantFilter, "TenantId")
                .WithMetadata(MetadataKeys.Metrics.Name, "orders_total")
                .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll)
                .WithMetadata(MetadataKeys.Metrics.SecurityMode, MetadataKeys.Metrics.SecurityModePerTenant))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_StrayMetricKeyWithoutName_Throws()
    {
        // metric-sum without metric-name: the author believes a metric is exported and
        // none is (nothing is exported absent the opt-in marker).
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Metrics.Sum, "Total"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Metrics.Name);
    }

    [Fact]
    public void Validate_UnknownMetricKey_Throws()
    {
        // A typo in the metric-* family must fail the model-load unknown-key gate.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Metrics.Name, "orders_total")
                .WithMetadata(MetadataKeys.Metrics.Count, MetadataKeys.Metrics.CountAll)
                .WithMetadata("metric-labls", "Total")) // typo
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("metric-labls").And.Contain("unrecognized");
    }
}
