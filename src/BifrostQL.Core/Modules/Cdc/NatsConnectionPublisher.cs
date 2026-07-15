using System;
using System.Threading;
using System.Threading.Tasks;
using NATS.Client.Core;

namespace BifrostQL.Core.Modules.Cdc
{
    /// <summary>
    /// The ONLY <see cref="INatsPublisher"/> that touches the concrete NATS client. It adapts an
    /// <see cref="INatsConnection"/>, publishing the raw envelope bytes to the given subject and
    /// carrying the CloudEvents id as the <c>Nats-Msg-Id</c> header so a JetStream consumer
    /// de-duplicates at-least-once redelivery. Deliberately tiny and NOT unit-tested (there is no
    /// live broker in tests); all sink logic that CAN be tested lives behind
    /// <see cref="INatsPublisher"/> in <see cref="NatsEventSink"/>.
    /// </summary>
    internal sealed class NatsConnectionPublisher : INatsPublisher
    {
        /// <summary>The NATS message header carrying the dedupe id (JetStream de-duplication).</summary>
        private const string MsgIdHeader = "Nats-Msg-Id";

        private readonly INatsConnection _connection;

        public NatsConnectionPublisher(INatsConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public ValueTask PublishAsync(string subject, byte[] payload, string msgId, CancellationToken ct)
        {
            var headers = new NatsHeaders { { MsgIdHeader, msgId } };
            return _connection.PublishAsync(subject, payload, headers: headers, cancellationToken: ct);
        }
    }
}
