using System.Net.WebSockets;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Scriptable WebSocket test double for exercising the binary transport send path
    /// (ChunkSender.SendChunksAsync / SendChunkedAsync) without a real network connection.
    ///
    /// Outgoing frames (SendAsync) are recorded in <see cref="SentFrames"/>.
    /// Incoming frames (ReceiveAsync) are pulled from a scripted queue; when the queue is
    /// drained, a default ChunkAck is returned so the sender never blocks indefinitely.
    /// </summary>
    internal sealed class FakeWebSocket : WebSocket
    {
        private readonly Queue<(WebSocketMessageType type, byte[] data)> _incoming = new();
        private WebSocketState _state = WebSocketState.Open;

        /// <summary>Every binary frame the sender pushed, in send order.</summary>
        public List<byte[]> SentFrames { get; } = new();

        /// <summary>Number of times ReceiveAsync was invoked (ACK reads).</summary>
        public int ReceiveCount { get; private set; }

        /// <summary>Default frame returned once the scripted queue is empty (defaults to a ChunkAck).</summary>
        public BifrostMessageType DefaultIncomingType { get; set; } = BifrostMessageType.ChunkAck;

        /// <summary>Queues a ChunkAck to be returned by the next ReceiveAsync.</summary>
        public void EnqueueAck(uint requestId = 0, uint sequence = 0)
        {
            var ack = new BifrostMessage
            {
                RequestId = requestId,
                Type = BifrostMessageType.ChunkAck,
                ChunkSequence = sequence,
            };
            _incoming.Enqueue((WebSocketMessageType.Binary, ack.ToBytes()));
        }

        /// <summary>Queues a ChunkNack (requests retransmission of a specific sequence).</summary>
        public void EnqueueNack(uint requestId, uint sequence)
        {
            var nack = new BifrostMessage
            {
                RequestId = requestId,
                Type = BifrostMessageType.ChunkNack,
                ChunkSequence = sequence,
            };
            _incoming.Enqueue((WebSocketMessageType.Binary, nack.ToBytes()));
        }

        /// <summary>Queues a Close frame; the next ReceiveAsync surfaces it as a close.</summary>
        public void EnqueueClose()
        {
            _incoming.Enqueue((WebSocketMessageType.Close, Array.Empty<byte>()));
        }

        /// <summary>Queues a non-binary (text) frame, which the ack reader treats as "no ack".</summary>
        public void EnqueueText()
        {
            _incoming.Enqueue((WebSocketMessageType.Text, new byte[] { 1 }));
        }

        public void ForceState(WebSocketState state) => _state = state;

        /// <summary>Re-deserializes the recorded frames into messages for assertions.</summary>
        public List<BifrostMessage> SentMessages()
            => SentFrames.Select(BifrostMessage.FromBytes).ToList();

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            var copy = new byte[buffer.Count];
            Buffer.BlockCopy(buffer.Array!, buffer.Offset, copy, 0, buffer.Count);
            SentFrames.Add(copy);
            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            ReceiveCount++;

            var (type, data) = _incoming.Count > 0
                ? _incoming.Dequeue()
                : DefaultFrame();

            if (type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true,
                    WebSocketCloseStatus.NormalClosure, "closed"));
            }

            Buffer.BlockCopy(data, 0, buffer.Array!, buffer.Offset, data.Length);
            return Task.FromResult(new WebSocketReceiveResult(data.Length, type, true));
        }

        private (WebSocketMessageType, byte[]) DefaultFrame()
        {
            var msg = new BifrostMessage { Type = DefaultIncomingType };
            return (WebSocketMessageType.Binary, msg.ToBytes());
        }

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }
    }
}
