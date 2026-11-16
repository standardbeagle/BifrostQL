using System.Threading.Channels;

namespace BifrostQL.Server
{
    /// <summary>
    /// One chunked response in flight on a binary connection. It sends its chunks through the
    /// connection's serialized send, keeps at most the ack window unacknowledged, and waits for
    /// acks on its own channel, which the connection's single reader fills through
    /// <see cref="AcknowledgeAsync"/>. Because the transfer never reads the socket, other requests
    /// on the connection execute and answer while it waits; their frames interleave with its
    /// chunks, and the client routes both by request id.
    /// </summary>
    public sealed class ChunkTransfer
    {
        private readonly IReadOnlyList<BifrostMessage> _chunks;
        private readonly int _ackWindow;
        private readonly TimeSpan _ackTimeout;
        private readonly int _maxRetransmitsPerSequence;
        private readonly ChunkBuffer? _chunkBuffer;
        private readonly Func<BifrostMessage, CancellationToken, Task> _send;

        // Acks the reader has routed here and the transfer has not consumed yet. A client acks
        // each chunk it receives once, so a valid client stays well under the bound; a client
        // that floods acks fills it and its reader waits, which slows only that connection.
        private readonly Channel<AckSignal> _signals;

        internal ChunkTransfer(
            IReadOnlyList<BifrostMessage> chunks,
            int ackWindow,
            TimeSpan ackTimeout,
            int maxRetransmitsPerSequence,
            ChunkBuffer? chunkBuffer,
            Func<BifrostMessage, CancellationToken, Task> send)
        {
            if (chunks.Count == 0) throw new ArgumentException("A transfer needs at least one chunk.", nameof(chunks));
            _chunks = chunks;
            _ackWindow = ackWindow;
            _ackTimeout = ackTimeout;
            _maxRetransmitsPerSequence = maxRetransmitsPerSequence;
            _chunkBuffer = chunkBuffer;
            _send = send;
            _signals = Channel.CreateBounded<AckSignal>(new BoundedChannelOptions(ackWindow * 4)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
        }

        // Sequences the client has acked, as routed by the reader. Only the reader touches it.
        private readonly HashSet<uint> _ackedByClient = new();

        /// <summary>The request this transfer answers.</summary>
        public uint RequestId => _chunks[0].RequestId;

        /// <summary>
        /// True once the client has acked every chunk: from its side the response is complete,
        /// and the transfer only has those acks left to consume. Read by the reader only.
        /// </summary>
        public bool AckedByClient => _ackedByClient.Count == _chunks.Count;

        /// <summary>
        /// Hands this transfer a ChunkAck or ChunkNack the client sent for it. Waits only while
        /// the client has flooded more acks than the transfer has consumed; an ack that arrives
        /// after the transfer ended is dropped.
        /// </summary>
        public async ValueTask AcknowledgeAsync(BifrostMessage frame, CancellationToken cancellationToken)
        {
            if (frame.Type is not (BifrostMessageType.ChunkAck or BifrostMessageType.ChunkNack))
                return;
            if (frame.Type == BifrostMessageType.ChunkAck && frame.ChunkSequence < _chunks.Count)
                _ackedByClient.Add(frame.ChunkSequence);
            try
            {
                await _signals.Writer.WriteAsync(
                    new AckSignal(frame.Type == BifrostMessageType.ChunkNack, frame.ChunkSequence), cancellationToken);
            }
            catch (ChannelClosedException)
            {
                // The transfer finished or failed; nothing waits for this ack.
            }
        }

        /// <summary>
        /// Sends every chunk, then waits until all are acknowledged so a late NACK on the tail
        /// is still served. Each chunk is stored in the connection's buffer as it goes.
        /// </summary>
        /// <exception cref="TimeoutException">The client stopped acknowledging for the ack timeout.</exception>
        /// <exception cref="ChunkRetransmitLimitExceededException">One chunk was NACKed past the cap.</exception>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                var unacked = 0;
                var retransmitCounts = new Dictionary<uint, int>();

                foreach (var chunk in _chunks)
                {
                    _chunkBuffer?.Add(chunk.RequestId, chunk.ChunkSequence, chunk);
                    await _send(chunk, cancellationToken);
                    unacked++;

                    while (unacked >= _ackWindow)
                        unacked += await NextSignalAsync(retransmitCounts, cancellationToken);
                }

                while (unacked > 0)
                    unacked += await NextSignalAsync(retransmitCounts, cancellationToken);
            }
            finally
            {
                // Releases a reader waiting on a full channel, and refuses late acks.
                _signals.Writer.TryComplete();
            }
        }

        /// <summary>
        /// Waits for one ack or NACK and returns the change to the unacknowledged count: -1 for
        /// an ack, +1 for a NACK whose retransmit is now in flight (its ack is owed too, which
        /// keeps the window bounded even when a client acks both copies), 0 for a NACK of a
        /// sequence this transfer does not have.
        /// </summary>
        private async Task<int> NextSignalAsync(Dictionary<uint, int> retransmitCounts, CancellationToken cancellationToken)
        {
            AckSignal signal;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(_ackTimeout);
                try
                {
                    signal = await _signals.Reader.ReadAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Client did not acknowledge chunked transfer within {_ackTimeout.TotalSeconds:0.###} seconds");
                }
            }

            if (!signal.IsNack)
                return -1;

            var count = retransmitCounts.TryGetValue(signal.Sequence, out var prior) ? prior + 1 : 1;
            retransmitCounts[signal.Sequence] = count;
            if (count > _maxRetransmitsPerSequence)
                throw new ChunkRetransmitLimitExceededException(RequestId, signal.Sequence, _maxRetransmitsPerSequence);

            if (signal.Sequence >= _chunks.Count)
                return 0;
            await _send(_chunks[(int)signal.Sequence], cancellationToken);
            return +1;
        }

        private readonly record struct AckSignal(bool IsNack, uint Sequence);
    }
}
