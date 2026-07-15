using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace BifrostQL.Core.Modules.Cdc
{
    /// <summary>
    /// The outcome a sink reports for one delivery attempt. A sink reports only
    /// success or a transient (retryable) failure — it never decides retry policy,
    /// never touches the outbox table, and never re-schedules. The dispatcher owns
    /// attempt counting and backoff; the sink owns only the wire.
    /// </summary>
    public enum EventDeliveryResult
    {
        /// <summary>The envelope was accepted by the downstream sink; the dispatcher stamps it dispatched.</summary>
        Delivered,

        /// <summary>The sink could not deliver right now (network, 5xx, throttle). The dispatcher
        /// increments the attempt counter and re-schedules with backoff. NOT a permanent error.</summary>
        TransientFailure,
    }

    /// <summary>
    /// A delivery target for CloudEvents drained from the transactional outbox. Every
    /// concrete sink (webhook, queue, …) implements this. The dispatcher
    /// (<see cref="OutboxDispatcher"/>) reads a row, maps it to a CloudEvents 1.0
    /// envelope (<see cref="CloudEventEnvelope.Build"/>), and hands the envelope to a
    /// sink.
    ///
    /// Contract: a sink NEVER retries (it returns <see cref="EventDeliveryResult.TransientFailure"/>
    /// and the dispatcher re-schedules), NEVER reads or writes the outbox table, and NEVER
    /// stamps <c>dispatched_at</c>/<c>attempts</c>. Those are the dispatcher's responsibility
    /// alone, so exactly-once bookkeeping lives in one place.
    /// </summary>
    public interface IEventSink
    {
        /// <summary>
        /// Attempts to deliver one CloudEvents envelope. Return
        /// <see cref="EventDeliveryResult.Delivered"/> on success or
        /// <see cref="EventDeliveryResult.TransientFailure"/> when the send should be
        /// retried later. Throwing is tolerated (the dispatcher treats an escaped
        /// exception as a transient failure and logs it server-side), but returning a
        /// result is preferred.
        ///
        /// <para><paramref name="idempotencyKey"/> is the CloudEvents <c>id</c> of the
        /// envelope (the stable, monotonic outbox row id). Delivery is <b>at-least-once</b>:
        /// the dispatcher can deliver a row and then crash before stamping
        /// <c>dispatched_at</c>, so the SAME event may be delivered again on a later pass.
        /// A sink (and its downstream consumer) MUST use this key to de-duplicate — the
        /// key is byte-for-byte identical across every redelivery of one event. Passing it
        /// through as the wire-level idempotency key (e.g. an HTTP <c>Idempotency-Key</c>
        /// header, or a message dedupe id) is how at-least-once becomes effectively
        /// exactly-once at the consumer.</para>
        /// </summary>
        ValueTask<EventDeliveryResult> DeliverAsync(
            JsonObject envelope, string idempotencyKey, CancellationToken cancellationToken);
    }
}
