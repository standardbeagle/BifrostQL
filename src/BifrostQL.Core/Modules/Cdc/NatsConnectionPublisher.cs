using System;
using System.Threading;
using System.Threading.Tasks;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace BifrostQL.Core.Modules.Cdc
{
    /// <summary>
    /// The ONLY <see cref="INatsPublisher"/> that touches the concrete NATS client. It publishes
    /// the raw envelope bytes to the given subject through JetStream and waits for the stream's
    /// <c>PubAck</c>, carrying the CloudEvents id as the JetStream message id (<c>Nats-Msg-Id</c>)
    /// so the stream de-duplicates at-least-once redelivery.
    ///
    /// <para><b>Why JetStream and not a core publish.</b> A core <c>PublishAsync</c> completes
    /// once the message is buffered in the client: no broker ever acknowledges it. The sink then
    /// reported <c>Delivered</c> and the outbox marked the row dispatched, so a broker restart, a
    /// dropped connection with buffered messages, or a subject no stream captures lost the event.
    /// A JetStream publish completes only on the stream's acknowledgement; a missing stream
    /// ("no responders"), a timeout or an error ack throws, and the sink maps that to a
    /// transient failure the dispatcher retries.</para>
    ///
    /// <para>Owns the connection it is given and disposes it with itself (the DI container
    /// disposes the sink, which disposes this).</para>
    /// </summary>
    internal sealed class NatsConnectionPublisher : INatsPublisher, IAsyncDisposable
    {
        private readonly NatsConnection _connection;
        private readonly NatsJSContext _jetStream;

        public NatsConnectionPublisher(NatsConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _jetStream = new NatsJSContext(connection);
        }

        public async ValueTask PublishAsync(string subject, byte[] payload, string msgId, CancellationToken ct)
        {
            var ack = await _jetStream.PublishAsync(subject, payload,
                opts: new NatsJSPubOpts { MsgId = msgId }, cancellationToken: ct);
            EnsureStored(ack);
        }

        /// <summary>
        /// Accepts an acknowledgement only when the stream stored the message. A duplicate ack
        /// means an earlier attempt was stored under the same message id, which is delivery.
        /// </summary>
        internal static void EnsureStored(PubAckResponse ack)
        {
            if (ack is null) throw new ArgumentNullException(nameof(ack));
            if (ack.Error is not null || string.IsNullOrEmpty(ack.Stream))
                throw new NatsJSException("The JetStream publish was not acknowledged by a stream.");
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
