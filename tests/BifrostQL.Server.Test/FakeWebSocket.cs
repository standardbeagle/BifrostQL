using System.Net.WebSockets;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Scriptable WebSocket test double for driving the binary transport middleware without a
    /// real network connection. It is safe for the middleware's one reader and one serialized
    /// writer to use at once.
    ///
    /// Outgoing frames (SendAsync) are recorded in <see cref="SentFrames"/>. Incoming frames
    /// (ReceiveAsync) come from a scripted queue. Once the queue is drained the fake acts like
    /// a client: it acks every chunk it was sent (by that chunk's request id and sequence),
    /// and sends Close once every Query, Mutation and Resume it delivered has a final answer.
    /// It echoes a close frame the server sends, as a real peer does.
    /// </summary>
    internal sealed class FakeWebSocket : WebSocket
    {
        private readonly Queue<(WebSocketMessageType type, byte[] data)> _incoming = new();
        private readonly Queue<BifrostMessage> _chunksToAck = new();
        private readonly HashSet<uint> _awaitingAnswer = new();
        private readonly object _gate = new();
        private readonly SemaphoreSlim _changed = new(0);
        private volatile WebSocketState _state = WebSocketState.Open;
        private bool _closeEchoPending;
        private (WebSocketMessageType type, byte[] data, int sent)? _partial;

        /// <summary>Every binary frame the server pushed, in send order.</summary>
        public List<byte[]> SentFrames { get; } = new();

        /// <summary>Number of times ReceiveAsync was invoked.</summary>
        public int ReceiveCount { get; private set; }

        /// <summary>
        /// When true, the drained fake never answers (no acks, no Close), honouring
        /// cancellation. Simulates a client that stops acknowledging.
        /// </summary>
        public bool HangWhenDrained { get; set; }

        /// <summary>
        /// When false (the default) the fake is a serial client: it delivers the next scripted
        /// frame only once every request it delivered has its final answer and every chunk is
        /// acked. When true it delivers the whole script at once, the way a pipelining client
        /// does.
        /// </summary>
        public bool Pipelined { get; init; }

        /// <summary>Raised with every frame the server sends, after it is recorded.</summary>
        public event Action<BifrostMessage>? Sent;

        /// <summary>
        /// When set, SendAsync throws ConnectionClosedPrematurely once this many frames
        /// have been sent. Simulates a client disconnect mid-transfer.
        /// </summary>
        public int? FailAfterSends { get; set; }

        /// <summary>
        /// When set, the send past this many frames never completes: a peer that stopped reading
        /// (a zero TCP window). The parked send honours cancellation the way a real WebSocket
        /// does, by aborting the socket. <see cref="SendStalled"/> completes once a send parks.
        /// </summary>
        public int? StallAfterSends { get; init; }

        private readonly TaskCompletionSource _sendStalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when a send parks under <see cref="StallAfterSends"/>.</summary>
        public Task SendStalled => _sendStalled.Task;

        /// <summary>
        /// When true, cancelling a parked receive aborts the socket, as a real WebSocket does
        /// (so no close frame can follow). Off by default for the facts written before it.
        /// </summary>
        public bool AbortOnCancelledReceive { get; init; }

        /// <summary>When true, the fake never echoes a close frame the server sends.</summary>
        public bool IgnoresClose { get; init; }

        private readonly TaskCompletionSource _receiveParked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closeFrameSent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once a receive parks waiting for the server.</summary>
        public Task ReceiveParked => _receiveParked.Task;

        /// <summary>Completes once the server sends a close frame (CloseOutputAsync).</summary>
        public Task CloseFrameSent => _closeFrameSent.Task;

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

        /// <summary>Queues a non-binary (text) frame.</summary>
        public void EnqueueText()
        {
            _incoming.Enqueue((WebSocketMessageType.Text, new byte[] { 1 }));
        }

        /// <summary>Queues any BifrostMessage as a binary frame.</summary>
        public void EnqueueMessage(BifrostMessage message)
        {
            _incoming.Enqueue((WebSocketMessageType.Binary, message.ToBytes()));
        }

        /// <summary>
        /// Delivers a Close frame while the connection runs (safe against the server's reader),
        /// even to a <see cref="HangWhenDrained"/> fake.
        /// </summary>
        public void DeliverClose()
        {
            lock (_gate)
            {
                _incoming.Enqueue((WebSocketMessageType.Close, Array.Empty<byte>()));
                _changed.Release();
            }
        }

        /// <summary>
        /// Delivers a message while the connection runs (safe against the server's reader),
        /// even to a <see cref="HangWhenDrained"/> fake: a client sending a new request while
        /// it still owes acks on another.
        /// </summary>
        public void DeliverMessage(BifrostMessage message)
        {
            lock (_gate)
            {
                _incoming.Enqueue((WebSocketMessageType.Binary, message.ToBytes()));
                _changed.Release();
            }
        }

        public void ForceState(WebSocketState state) => _state = state;

        /// <summary>Re-deserializes the recorded frames into messages for assertions.</summary>
        public List<BifrostMessage> SentMessages()
        {
            lock (_gate)
                return SentFrames.Select(BifrostMessage.FromBytes).ToList();
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (StallAfterSends.HasValue && SentFrames.Count >= StallAfterSends.Value)
                    return StallAsync(cancellationToken);

                if (FailAfterSends.HasValue && SentFrames.Count >= FailAfterSends.Value)
                {
                    _state = WebSocketState.Aborted;
                    _changed.Release();
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
                }

                var copy = buffer.AsSpan().ToArray();
                SentFrames.Add(copy);
                var sent = BifrostMessage.FromBytes(copy);
                switch (sent.Type)
                {
                    case BifrostMessageType.Chunk:
                        _chunksToAck.Enqueue(sent);
                        break;
                    case BifrostMessageType.Result:
                    case BifrostMessageType.Error:
                        _awaitingAnswer.Remove(sent.RequestId);
                        break;
                    case BifrostMessageType.ResumeAck when sent.ChunkTotal == 0:
                        _awaitingAnswer.Remove(sent.RequestId);
                        break;
                }
                _changed.Release();
                Sent?.Invoke(sent);
            }
            return Task.CompletedTask;
        }

        private async Task StallAsync(CancellationToken cancellationToken)
        {
            _sendStalled.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // A real WebSocket aborts itself when a pending operation is cancelled.
                Abort();
                throw;
            }
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            ReceiveCount++;
            if (_partial is { } rest)
                return DeliverPart(rest.type, rest.data, rest.sent, buffer);
            while (true)
            {
                (WebSocketMessageType type, byte[] data)? next = null;
                lock (_gate)
                {
                    if (_state == WebSocketState.Aborted)
                        throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
                    if (_closeEchoPending)
                    {
                        _closeEchoPending = false;
                        _state = WebSocketState.Closed;
                        next = (WebSocketMessageType.Close, Array.Empty<byte>());
                    }
                    else if (_incoming.Count > 0
                        && (Pipelined || HangWhenDrained || (_awaitingAnswer.Count == 0 && _chunksToAck.Count == 0)))
                    {
                        next = _incoming.Dequeue();
                        TrackDelivered(next.Value);
                    }
                    else if (!HangWhenDrained && _chunksToAck.Count > 0)
                    {
                        next = (WebSocketMessageType.Binary, AckFor(_chunksToAck.Dequeue()).ToBytes());
                    }
                    else if (!HangWhenDrained && _awaitingAnswer.Count == 0)
                    {
                        next = (WebSocketMessageType.Close, Array.Empty<byte>());
                    }
                }

                if (next is { } frame)
                {
                    if (frame.type == WebSocketMessageType.Close)
                    {
                        if (_state == WebSocketState.Open)
                            _state = WebSocketState.CloseReceived;
                        return new WebSocketReceiveResult(
                            0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "closed");
                    }
                    return DeliverPart(frame.type, frame.data, 0, buffer);
                }

                // Nothing to answer yet: wait for the server to send something.
                _receiveParked.TrySetResult();
                try
                {
                    await _changed.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (AbortOnCancelledReceive)
                {
                    // A real WebSocket aborts itself when a pending receive is cancelled.
                    Abort();
                    throw;
                }
            }
        }

        /// <summary>
        /// Copies as much of a scripted message as the receive buffer holds, as a real socket
        /// does: a message larger than the buffer arrives over several receives.
        /// </summary>
        private WebSocketReceiveResult DeliverPart(
            WebSocketMessageType type, byte[] data, int sent, ArraySegment<byte> buffer)
        {
            var count = Math.Min(buffer.Count, data.Length - sent);
            data.AsSpan(sent, count).CopyTo(buffer.AsSpan());
            var end = sent + count == data.Length;
            _partial = end ? null : (type, data, sent + count);
            return new WebSocketReceiveResult(count, type, end);
        }

        /// <summary>
        /// Acks a chunk, and marks its request answered once the final chunk is acked.
        /// Caller holds the gate.
        /// </summary>
        private BifrostMessage AckFor(BifrostMessage chunk)
        {
            if (chunk.ChunkSequence + 1 == chunk.ChunkTotal)
                _awaitingAnswer.Remove(chunk.RequestId);
            return ChunkReceiver.CreateAck(chunk.RequestId, chunk.ChunkSequence);
        }

        private void TrackDelivered((WebSocketMessageType type, byte[] data) frame)
        {
            if (frame.type != WebSocketMessageType.Binary)
                return;
            BifrostMessage delivered;
            try
            {
                delivered = BifrostMessage.FromBytes(frame.data);
            }
            catch (Exception)
            {
                return; // a garbage frame a test sends on purpose
            }
            if (delivered.Type is BifrostMessageType.Query or BifrostMessageType.Mutation or BifrostMessageType.Resume)
                _awaitingAnswer.Add(delivered.RequestId);
        }

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _changed.Release();
        }
        public override void Dispose() { }

        /// <summary>The status the server closed with, or null while the socket is open.</summary>
        public WebSocketCloseStatus? ClosedWith { get; private set; }

        /// <summary>The reason text the server closed with.</summary>
        public string? ClosedReason { get; private set; }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            ClosedWith = closeStatus;
            ClosedReason = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ClosedWith = closeStatus;
                ClosedReason = statusDescription;
                _state = _state == WebSocketState.CloseReceived ? WebSocketState.Closed : WebSocketState.CloseSent;
                // A peer answers a close frame with its own.
                _closeEchoPending = _state == WebSocketState.CloseSent && !IgnoresClose;
                _changed.Release();
            }
            _closeFrameSent.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
