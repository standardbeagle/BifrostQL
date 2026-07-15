using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Cdc;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for CDC slice 7 — the NATS <see cref="IEventSink"/> (second sink after the webhook).
/// The subject is a PURE function of the envelope (its CloudEvents <c>type</c> verbatim), so it is
/// unit-testable with no broker. Delivery goes through a fake <see cref="INatsPublisher"/> that
/// records the subject/payload/msgId (or throws a supplied exception) — no live NATS server. The
/// sink owns only the wire: success ⇒ Delivered, any publish fault ⇒ TransientFailure, cancellation
/// propagates.
/// </summary>
public class NatsEventSinkTests
{
    // A representative outbox row, built through the real CloudEventEnvelope builder so the test
    // pins the ACTUAL envelope shape rather than a hand-rolled one.
    private static JsonObject Envelope(string op = "insert", string aggregate = "dbo.orders")
    {
        var row = new Dictionary<string, object?>
        {
            [MetadataKeys.Cdc.ColId] = 4821L,
            [MetadataKeys.Cdc.ColAggregate] = aggregate,
            [MetadataKeys.Cdc.ColOp] = op,
            [MetadataKeys.Cdc.ColPayload] = "{\"id\":1007,\"status\":\"shipped\"}",
            [MetadataKeys.Cdc.ColTenant] = "acme",
            [MetadataKeys.Cdc.ColCreatedAt] = new DateTime(2026, 7, 11, 22, 4, 11, DateTimeKind.Utc),
        };
        return CloudEventEnvelope.Build(row, subject: "1007");
    }

    [Fact]
    public void SubjectFor_returns_the_envelope_type_verbatim()
    {
        // The type is bifrostql.<schema>.<table>.<op> — a valid NATS subject as-is.
        NatsEventSink.SubjectFor(Envelope("insert")).Should().Be("bifrostql.dbo.orders.insert");
        NatsEventSink.SubjectFor(Envelope("update")).Should().Be("bifrostql.dbo.orders.update");
    }

    [Fact]
    public void SubjectFor_throws_when_type_is_absent()
    {
        var envelope = new JsonObject { ["id"] = "1" }; // no "type"
        var act = () => NatsEventSink.SubjectFor(envelope);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Successful_publish_reports_delivered_with_matching_subject_and_dedupe_id()
    {
        var publisher = new RecordingPublisher();
        var sink = new NatsEventSink(publisher);

        var result = await sink.DeliverAsync(Envelope(), "4821", CancellationToken.None);

        result.Should().Be(EventDeliveryResult.Delivered);
        publisher.Subject.Should().Be("bifrostql.dbo.orders.insert");
        // The CloudEvents id is threaded through as the NATS dedupe id.
        publisher.MsgId.Should().Be("4821");
        // The published bytes are the exact serialized envelope.
        publisher.Payload.Should().Equal(Encoding.UTF8.GetBytes(Envelope().ToJsonString()));
    }

    [Fact]
    public async Task Publisher_exception_is_mapped_to_transient_failure_and_swallowed()
    {
        var publisher = new ThrowingPublisher(new InvalidOperationException("broker unreachable at 10.0.0.7"));
        var sink = new NatsEventSink(publisher);

        var result = await sink.DeliverAsync(Envelope(), "4821", CancellationToken.None);

        result.Should().Be(EventDeliveryResult.TransientFailure);
    }

    [Fact]
    public async Task Cancellation_propagates_rather_than_masquerading_as_transient_failure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var publisher = new ThrowingPublisher(new OperationCanceledException());
        var sink = new NatsEventSink(publisher);

        var act = () => sink.DeliverAsync(Envelope(), "4821", cts.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_idempotency_key_is_rejected_before_any_publish(string? key)
    {
        var publisher = new RecordingPublisher();
        var sink = new NatsEventSink(publisher);

        var act = () => sink.DeliverAsync(Envelope(), key!, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<ArgumentException>();
        publisher.WasCalled.Should().BeFalse("a blank dedupe id must never reach the wire");
    }

    private sealed class RecordingPublisher : INatsPublisher
    {
        public bool WasCalled { get; private set; }
        public string? Subject { get; private set; }
        public byte[]? Payload { get; private set; }
        public string? MsgId { get; private set; }

        public ValueTask PublishAsync(string subject, byte[] payload, string msgId, CancellationToken ct)
        {
            WasCalled = true;
            Subject = subject;
            Payload = payload;
            MsgId = msgId;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingPublisher : INatsPublisher
    {
        private readonly Exception _toThrow;
        public ThrowingPublisher(Exception toThrow) => _toThrow = toThrow;

        public ValueTask PublishAsync(string subject, byte[] payload, string msgId, CancellationToken ct)
            => throw _toThrow;
    }
}
