using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Cdc;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for CDC slice 1 — the metadata contract parsed from
/// <c>emit-events</c> / <c>event-sink</c> / <c>event-payload</c> into a typed
/// <see cref="CdcEventConfig"/>. Downstream slices (the before-commit writer and
/// dispatcher) consume this config, so its parse and its fail-fast behavior on
/// typo'd tokens are pinned here.
/// </summary>
public class CdcEventConfigTests
{
    private static IDbTable TableWithMetadata(params (string key, object? value)[] metadata)
    {
        var table = Substitute.For<IDbTable>();
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in metadata)
            dict[key] = value;
        table.DbName.Returns("orders");
        table.TableSchema.Returns("dbo");
        table.Metadata.Returns(dict);
        table.GetMetadataValue(Arg.Any<string>())
            .Returns(ci => dict.TryGetValue((string)ci[0], out var v) ? v?.ToString() : null);
        return table;
    }

    [Fact]
    public void FromTable_NoEmitEvents_ReturnsNone()
    {
        var table = TableWithMetadata();

        var config = CdcEventConfig.FromTable(table);

        config.EmitsEvents.Should().BeFalse();
        config.Should().BeSameAs(CdcEventConfig.None);
    }

    [Fact]
    public void FromTable_AllOperations_ParsesEachAndDefaults()
    {
        // Arrange: opt in to all three operations, no sink/payload → defaults.
        var table = TableWithMetadata(
            (MetadataKeys.Cdc.EmitEvents, "insert,update,delete"));

        // Act
        var config = CdcEventConfig.FromTable(table);

        // Assert
        config.EmitsEvents.Should().BeTrue();
        config.Emits(MutationType.Insert).Should().BeTrue();
        config.Emits(MutationType.Update).Should().BeTrue();
        config.Emits(MutationType.Delete).Should().BeTrue();
        config.Sink.Should().Be(CdcEventSink.Outbox);          // default sink
        config.PayloadMode.Should().Be(CdcPayloadMode.Full);    // default payload
    }

    [Fact]
    public void FromTable_SubsetOfOperations_OnlyThoseEmit()
    {
        var table = TableWithMetadata(
            (MetadataKeys.Cdc.EmitEvents, "insert, delete"));

        var config = CdcEventConfig.FromTable(table);

        config.Emits(MutationType.Insert).Should().BeTrue();
        config.Emits(MutationType.Delete).Should().BeTrue();
        config.Emits(MutationType.Update).Should().BeFalse();
    }

    [Theory]
    [InlineData("full", CdcPayloadMode.Full)]
    [InlineData("changed", CdcPayloadMode.Changed)]
    [InlineData("keys", CdcPayloadMode.Keys)]
    [InlineData("KEYS", CdcPayloadMode.Keys)] // case-insensitive
    public void FromTable_PayloadMode_Parses(string raw, CdcPayloadMode expected)
    {
        var table = TableWithMetadata(
            (MetadataKeys.Cdc.EmitEvents, "update"),
            (MetadataKeys.Cdc.EventPayload, raw));

        var config = CdcEventConfig.FromTable(table);

        config.PayloadMode.Should().Be(expected);
    }

    [Fact]
    public void FromTable_ExplicitOutboxSink_Parses()
    {
        var table = TableWithMetadata(
            (MetadataKeys.Cdc.EmitEvents, "insert"),
            (MetadataKeys.Cdc.EventSink, "outbox"));

        var config = CdcEventConfig.FromTable(table);

        config.Sink.Should().Be(CdcEventSink.Outbox);
    }

    [Fact]
    public void FromTable_UnknownOperation_Throws()
    {
        // A typo'd operation must fail fast — silently dropping it would mean the
        // table never emits the intended event with no error.
        var table = TableWithMetadata(
            (MetadataKeys.Cdc.EmitEvents, "insert,updte"));

        var act = () => CdcEventConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("updte").And.Contain(MetadataKeys.Cdc.EmitEvents);
    }

    [Theory]
    [InlineData(",")]
    [InlineData(", ,")]
    [InlineData("  ")]
    public void FromTable_EmitEventsPresentButNoValidOperation_Throws(string raw)
    {
        // 'emit-events' present (parser reached) but yielding zero operations must
        // fail fast — it must NOT silently produce EmitsEvents=false (fail-open).
        // A whitespace-only value ("  ") is treated as absent (returns None), so
        // only the punctuation-only cases reach the guard; assert the intent per case.
        var table = TableWithMetadata((MetadataKeys.Cdc.EmitEvents, raw));

        if (string.IsNullOrWhiteSpace(raw))
        {
            // Whitespace-only reads as "key not meaningfully set" → None, not a throw.
            CdcEventConfig.FromTable(table).EmitsEvents.Should().BeFalse();
            return;
        }

        var act = () => CdcEventConfig.FromTable(table);
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Cdc.EmitEvents).And.Contain("no valid operation");
    }

    [Fact]
    public void FromTable_UnknownSink_Throws()
    {
        var table = TableWithMetadata(
            (MetadataKeys.Cdc.EmitEvents, "insert"),
            (MetadataKeys.Cdc.EventSink, "kafka"));

        var act = () => CdcEventConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("kafka").And.Contain(MetadataKeys.Cdc.EventSink);
    }

    [Fact]
    public void FromTable_UnknownPayloadMode_Throws()
    {
        var table = TableWithMetadata(
            (MetadataKeys.Cdc.EmitEvents, "insert"),
            (MetadataKeys.Cdc.EventPayload, "diff"));

        var act = () => CdcEventConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("diff").And.Contain(MetadataKeys.Cdc.EventPayload);
    }
}
