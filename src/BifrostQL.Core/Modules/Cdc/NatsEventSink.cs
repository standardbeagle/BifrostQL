using System;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Modules.Cdc
{
    /// <summary>
    /// Thin publisher seam the <see cref="NatsEventSink"/> depends on, so the sink's
    /// error-mapping and subject logic are unit-testable with a fake and no live broker.
    /// The ONLY implementation that touches the concrete NATS client is
    /// <see cref="NatsConnectionPublisher"/>.
    /// </summary>
    internal interface INatsPublisher
    {
        /// <param name="subject">The NATS subject (the CloudEvents <c>type</c>).</param>
        /// <param name="payload">The serialized envelope bytes, published verbatim.</param>
        /// <param name="msgId">The CloudEvents <c>id</c>, carried as the <c>Nats-Msg-Id</c>
        /// header so a JetStream consumer de-duplicates at-least-once redelivery.</param>
        ValueTask PublishAsync(string subject, byte[] payload, string msgId, CancellationToken ct);
    }

    /// <summary>
    /// An <see cref="IEventSink"/> that publishes each CloudEvents envelope drained from the
    /// transactional outbox to a NATS subject. Owns only the wire: it never retries, never
    /// re-schedules, never touches the outbox — any publish fault becomes
    /// <see cref="EventDeliveryResult.TransientFailure"/> and the
    /// <see cref="OutboxDispatcher"/> decides backoff / dead-letter.
    ///
    /// <para><b>Subject.</b> The subject is the envelope's <c>type</c> verbatim
    /// (<c>bifrostql.&lt;schema&gt;.&lt;table&gt;.&lt;op&gt;</c>, already a valid NATS subject).
    /// <see cref="SubjectFor"/> is a pure function of the envelope — no connection needed.</para>
    ///
    /// <para><b>Idempotency.</b> The CloudEvents <c>id</c> (<paramref name="idempotencyKey"/> on
    /// <see cref="DeliverAsync"/>) becomes the NATS message dedupe id (<c>Nats-Msg-Id</c> header on
    /// the real adapter), so at-least-once redelivery de-dupes at a JetStream consumer.</para>
    ///
    /// <para><b>Error hygiene.</b> A publish fault is logged by exception TYPE only — never a
    /// Bifrost-internal message or the payload (protocol-adapter-security rule 3). Cancellation via
    /// the passed token rethrows so the dispatcher's loop observes cooperative shutdown.</para>
    /// </summary>
    public sealed class NatsEventSink : IEventSink
    {
        /// <summary>Configuration key naming the NATS server URL the sink connects to.</summary>
        public const string UrlConfigKey = "Cdc:NatsUrl";

        private readonly INatsPublisher _publisher;
        private readonly ILogger? _logger;

        internal NatsEventSink(INatsPublisher publisher, ILogger? logger = null)
        {
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
            _logger = logger;
        }

        /// <inheritdoc />
        public async ValueTask<EventDeliveryResult> DeliverAsync(
            JsonObject envelope, string idempotencyKey, CancellationToken cancellationToken)
        {
            if (envelope is null) throw new ArgumentNullException(nameof(envelope));
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));

            var subject = SubjectFor(envelope);

            // Serialize ONCE and publish the exact bytes, mirroring the webhook sink.
            var body = Encoding.UTF8.GetBytes(envelope.ToJsonString());

            try
            {
                await _publisher.PublishAsync(subject, body, msgId: idempotencyKey, cancellationToken);
                return EventDeliveryResult.Delivered;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Cooperative shutdown — let the dispatcher's loop observe cancellation.
            }
            catch (Exception ex)
            {
                // Connection / timeout / broker fault: transient. Log the exception TYPE only —
                // never a Bifrost-internal message or the payload onto any surface.
                _logger?.LogWarning("CDC NATS delivery failed ({Error}); will retry.", ex.GetType().Name);
                return EventDeliveryResult.TransientFailure;
            }
        }

        /// <summary>
        /// The NATS subject for one envelope: its CloudEvents <c>type</c> verbatim, which the
        /// <see cref="CloudEventEnvelope"/> already builds as
        /// <c>bifrostql.&lt;schema&gt;.&lt;table&gt;.&lt;op&gt;</c> — itself a valid NATS subject.
        /// Pure function of the envelope; the <c>schema.table.op</c> is never re-derived from
        /// other fields. Throws <see cref="ArgumentException"/> when <c>type</c> is absent/blank.
        /// </summary>
        internal static string SubjectFor(JsonObject envelope)
        {
            if (envelope is null) throw new ArgumentNullException(nameof(envelope));

            var type = envelope.TryGetPropertyValue("type", out var node) ? node?.GetValue<string>() : null;
            if (string.IsNullOrWhiteSpace(type))
                throw new ArgumentException(
                    "Envelope is missing a 'type' to use as the NATS subject.", nameof(envelope));
            return type;
        }
    }
}
