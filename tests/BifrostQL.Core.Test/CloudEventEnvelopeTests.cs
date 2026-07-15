using System;
using System.Collections.Generic;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Cdc;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for CDC slice 3 — the pure CloudEvents 1.0 envelope builder that maps
/// one drained outbox row (the <see cref="MetadataKeys.Cdc.OutboxColumns"/> contract)
/// to a CloudEvents JSON envelope. The builder is pure: it takes <c>time</c> from the
/// row (never the wall clock), performs no I/O, and places only the captured
/// <c>payload</c> into the event body — never internal exception text or driver detail.
/// </summary>
public class CloudEventEnvelopeTests
{
    // A fixed UTC instant so the emitted `time` is deterministic and asserts exactly.
    private static readonly DateTime CreatedAt =
        new(2026, 7, 11, 22, 4, 11, DateTimeKind.Utc);

    private static Dictionary<string, object?> OutboxRow(
        string op, string aggregate = "dbo.orders", object? tenant = null,
        long id = 4821, string payload = "{\"id\":1007,\"status\":\"shipped\"}")
        => new()
        {
            [MetadataKeys.Cdc.ColId] = id,
            [MetadataKeys.Cdc.ColAggregate] = aggregate,
            [MetadataKeys.Cdc.ColOp] = op,
            [MetadataKeys.Cdc.ColPayload] = payload,
            [MetadataKeys.Cdc.ColTenant] = tenant,
            [MetadataKeys.Cdc.ColCreatedAt] = CreatedAt,
        };

    [Theory]
    [InlineData("insert")]
    [InlineData("update")]
    [InlineData("delete")]
    public void Build_emits_valid_cloudevents_1_0_shape_for_each_op(string op)
    {
        // Arrange
        var row = OutboxRow(op, tenant: "acme");

        // Act
        var e = CloudEventEnvelope.Build(row, subject: "1007");

        // Assert — required CloudEvents 1.0 attributes.
        e["specversion"]!.GetValue<string>().Should().Be("1.0");
        e["id"]!.GetValue<string>().Should().Be("4821");
        e["source"]!.GetValue<string>().Should().Be("dbo.orders");
        e["type"]!.GetValue<string>().Should().Be($"bifrostql.dbo.orders.{op}");
        e["subject"]!.GetValue<string>().Should().Be("1007");
        e["datacontenttype"]!.GetValue<string>().Should().Be("application/json");

        // time is the row's created_at rendered as RFC3339 UTC (trailing Z), not the wall clock.
        e["time"]!.GetValue<string>().Should().StartWith("2026-07-11T22:04:11").And.EndWith("Z");

        // tenant travels as a lowercase-named extension attribute.
        e["tenant"]!.GetValue<string>().Should().Be("acme");

        // The event body is exactly the captured payload — nothing else.
        e["data"]!["id"]!.GetValue<int>().Should().Be(1007);
        e["data"]!["status"]!.GetValue<string>().Should().Be("shipped");
    }

    [Fact]
    public void Build_omits_tenant_extension_entirely_when_row_has_no_tenant()
    {
        // Arrange — a row with no tenant (DBNull, as a nullable DB column reads back).
        var row = OutboxRow("insert", tenant: DBNull.Value);

        // Act
        var e = CloudEventEnvelope.Build(row, subject: "1007");

        // Assert — the attribute is absent, never emitted as null.
        e.ContainsKey("tenant").Should().BeFalse();
        // The rest of the envelope is still well-formed.
        e["type"]!.GetValue<string>().Should().Be("bifrostql.dbo.orders.insert");
        e["data"]!["id"]!.GetValue<int>().Should().Be(1007);
    }

    [Fact]
    public void Build_omits_tenant_extension_when_tenant_key_absent()
    {
        // Arrange — the key is missing outright rather than null.
        var row = OutboxRow("update");
        row.Remove(MetadataKeys.Cdc.ColTenant);

        // Act
        var e = CloudEventEnvelope.Build(row, subject: "1007");

        // Assert
        e.ContainsKey("tenant").Should().BeFalse();
    }
}
