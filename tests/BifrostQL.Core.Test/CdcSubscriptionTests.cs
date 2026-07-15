using System.Text.Json.Nodes;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Cdc;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for CDC slice 6 — the model-level subscription that scopes outbound
/// delivery BEFORE the sink. Pins the load-bearing backward-compat default
/// (no subscription ⇒ deliver-all, redact no-op), the fail-closed allow-list and
/// tenant gating, and the key-preserving redaction.
/// </summary>
public class CdcSubscriptionTests
{
    private static IDbModel ModelWithMetadata(params (string key, string? value)[] metadata)
    {
        var model = Substitute.For<IDbModel>();
        var dict = new Dictionary<string, string?>();
        foreach (var (key, value) in metadata)
            dict[key] = value;
        model.GetMetadataValue(Arg.Any<string>())
            .Returns(ci => dict.TryGetValue((string)ci[0], out var v) ? v : null);
        return model;
    }

    [Fact]
    public void FromModel_NoSubscriptionKeys_IsUnrestrictedAndDeliversAll()
    {
        var subscription = CdcSubscription.FromModel(ModelWithMetadata());

        subscription.Active.Should().BeFalse();
        subscription.Should().BeSameAs(CdcSubscription.Unrestricted);
        // Deliver-all regardless of aggregate/tenant — the pre-subscription behaviour.
        subscription.Delivers("dbo.anything", rowTenant: null).Should().BeTrue();
        subscription.Delivers("dbo.other", rowTenant: "whatever").Should().BeTrue();
    }

    [Fact]
    public void Unrestricted_Redact_IsANoOp()
    {
        var payload = new JsonObject { ["id"] = 1, ["ssn"] = "123-45-6789" };

        var result = CdcSubscription.Unrestricted.Redact(payload, new[] { "id" });

        result.Should().BeSameAs(payload);
        result.ContainsKey("ssn").Should().BeTrue("an inactive subscription never redacts");
    }

    [Fact]
    public void ActiveEmptyAllowList_DeliversNothing_FailClosed()
    {
        // Active (a tenant is bound) but NO allow-listed tables → fail-closed: nothing
        // delivers, even a row whose tenant matches the binding.
        var subscription = CdcSubscription.FromModel(
            ModelWithMetadata((MetadataKeys.Cdc.SubscriptionTenant, "acme")));

        subscription.Active.Should().BeTrue();
        subscription.Delivers("dbo.orders", "acme").Should().BeFalse("empty allow-list ⇒ deliver nothing");
        subscription.Delivers("dbo.widgets", "acme").Should().BeFalse();
    }

    [Fact]
    public void AllowList_GatesByAggregate()
    {
        var subscription = CdcSubscription.FromModel(
            ModelWithMetadata((MetadataKeys.Cdc.SubscriptionTables, "dbo.orders, dbo.widgets")));

        subscription.Delivers("dbo.orders", rowTenant: null).Should().BeTrue();
        subscription.Delivers("DBO.WIDGETS", rowTenant: null).Should().BeTrue("names compare normalized");
        subscription.Delivers("dbo.secrets", rowTenant: null).Should().BeFalse("not on the allow-list");
    }

    [Fact]
    public void TenantBound_DeliversOnlyMatchingTenant_NeverNullTenant()
    {
        var subscription = CdcSubscription.FromModel(ModelWithMetadata(
            (MetadataKeys.Cdc.SubscriptionTables, "dbo.orders"),
            (MetadataKeys.Cdc.SubscriptionTenant, "tenant-x")));

        subscription.Delivers("dbo.orders", "tenant-x").Should().BeTrue("tenant matches the binding");
        subscription.Delivers("dbo.orders", "tenant-y").Should().BeFalse("a different tenant is excluded");
        subscription.Delivers("dbo.orders", rowTenant: null).Should().BeFalse("a null-tenant row is never delivered to a bound subscription");
        subscription.Delivers("dbo.orders", "  ").Should().BeFalse("a blank-tenant row is never delivered to a bound subscription");
    }

    [Fact]
    public void Redact_StripsListedColumns_ButNeverAKeyColumn()
    {
        var subscription = CdcSubscription.FromModel(ModelWithMetadata(
            (MetadataKeys.Cdc.SubscriptionTables, "dbo.orders"),
            (MetadataKeys.Cdc.SubscriptionRedact, "ssn, id"))); // 'id' is also the key

        var payload = new JsonObject
        {
            ["id"] = 7,
            ["ssn"] = "123-45-6789",
            ["name"] = "Ada",
        };

        var result = subscription.Redact(payload, new[] { "id" });

        result.ContainsKey("ssn").Should().BeFalse("a listed non-key column is stripped");
        result.ContainsKey("id").Should().BeTrue("a key column is never stripped even when listed");
        result.ContainsKey("name").Should().BeTrue("an unlisted column is untouched");
    }

    [Fact]
    public void Redact_NoOp_WhenRedactListEmpty()
    {
        var subscription = CdcSubscription.FromModel(
            ModelWithMetadata((MetadataKeys.Cdc.SubscriptionTables, "dbo.orders")));

        var payload = new JsonObject { ["id"] = 1, ["ssn"] = "x" };
        var result = subscription.Redact(payload, new[] { "id" });

        result.ContainsKey("ssn").Should().BeTrue("no redaction list ⇒ no columns stripped");
    }

    [Theory]
    [InlineData(",")]
    [InlineData(", ,")]
    public void FromModel_PresentButEmptyAllowList_Throws(string raw)
    {
        var act = () => CdcSubscription.FromModel(
            ModelWithMetadata((MetadataKeys.Cdc.SubscriptionTables, raw)));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Cdc.SubscriptionTables);
    }
}
