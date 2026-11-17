using System.IO.Hashing;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
    /// <summary>
    /// Splits large BifrostMessages into chunked frames and sends them over a WebSocket
    /// with backpressure control. The full message is first serialized to bytes, then
    /// those bytes are split into chunk-sized fragments. The receiver reassembles the
    /// fragments and deserializes to recover the original message.
    ///
    /// A transfer pauses when unacknowledged chunks reach the configured window and resumes
    /// as ChunkAck messages arrive (see <see cref="ChunkTransfer"/>). By default the pause has
    /// no time limit (<see cref="NoAckTimeout"/>).
    /// </summary>
    public sealed class ChunkSender
    {
        /// <summary>
        /// Default chunk size threshold: serialized messages larger than this are chunked (64 KB).
        /// </summary>
        public const int DefaultChunkThreshold = 64 * 1024;

        /// <summary>
        /// Default maximum number of unacknowledged chunks before the sender pauses.
        /// </summary>
        public const int DefaultAckWindow = 8;

        /// <summary>
        /// The ack timeout that means "no stall cap" (<see cref="Timeout.InfiniteTimeSpan"/>), and
        /// the default. A full ack window is backpressure: the transfer pauses until the client
        /// acks, however long its consumer takes. A slow consumer is not a dead peer, and a
        /// per-ack timer never bounded how long a client holds a transfer anyway (one ack just
        /// under the timer keeps it alive). A peer that goes silent is cut off by the
        /// connection's idle deadline, which ends every transfer on it.
        /// </summary>
        public static readonly TimeSpan NoAckTimeout = Timeout.InfiniteTimeSpan;

        /// <summary>
        /// Default cap on how many times a single chunk sequence may be retransmitted in
        /// response to NACKs during one transfer. A client that perpetually NACKs the same
        /// sequence never lets the ack window drain, so without a cap the sender would
        /// retransmit forever (each round merely resetting the ack timeout). Once the cap is
        /// exceeded the transfer is aborted with an error.
        /// </summary>
        public const int DefaultMaxRetransmitsPerSequence = 5;

        private readonly int _chunkThreshold;
        private readonly int _ackWindow;
        private readonly ChunkBuffer? _chunkBuffer;
        private readonly TimeSpan _ackTimeout;
        private readonly int _maxRetransmitsPerSequence;
        private readonly ILogger? _logger;
        private readonly TimeProvider _timeProvider;

        public ChunkSender(int chunkThreshold = DefaultChunkThreshold, int ackWindow = DefaultAckWindow)
            : this(chunkThreshold, ackWindow, null)
        {
        }

        /// <param name="ackTimeout">
        /// How long a transfer waits for the next ack once its window is full before it gives up
        /// with a <see cref="TimeoutException"/>. Null or <see cref="NoAckTimeout"/> (the default)
        /// waits as long as the client takes.
        /// </param>
        /// <param name="timeProvider">The clock the ack timeout runs on; the system clock when null.</param>
        public ChunkSender(
            int chunkThreshold,
            int ackWindow,
            ChunkBuffer? chunkBuffer,
            TimeSpan? ackTimeout = null,
            ILogger? logger = null,
            int maxRetransmitsPerSequence = DefaultMaxRetransmitsPerSequence,
            TimeProvider? timeProvider = null)
        {
            if (chunkThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(chunkThreshold));
            if (ackWindow <= 0) throw new ArgumentOutOfRangeException(nameof(ackWindow));
            if (ackTimeout is { } stated && stated <= TimeSpan.Zero && stated != NoAckTimeout)
                throw new ArgumentOutOfRangeException(nameof(ackTimeout));
            if (maxRetransmitsPerSequence <= 0) throw new ArgumentOutOfRangeException(nameof(maxRetransmitsPerSequence));
            _chunkThreshold = chunkThreshold;
            _ackWindow = ackWindow;
            _chunkBuffer = chunkBuffer;
            _ackTimeout = ackTimeout ?? NoAckTimeout;
            _maxRetransmitsPerSequence = maxRetransmitsPerSequence;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>
        /// Whether a message requires chunking. A message is chunked when its serialized
        /// byte representation exceeds the configured threshold. Only Result messages are
        /// checked, since they carry the large data payloads.
        /// </summary>
        public bool RequiresChunking(BifrostMessage response)
        {
            return response.Type == BifrostMessageType.Result && response.Payload.Length > _chunkThreshold;
        }

        /// <summary>
        /// Splits the message's serialized bytes into chunk messages. Each chunk carries a
        /// fragment of the full serialized message with its CRC32 checksum, sequence number,
        /// byte offset, and total metadata. The receiver reassembles the fragments and
        /// deserializes via BifrostMessage.FromBytes. The serialized message is the envelope
        /// followed by the payload, so chunks copy from those two arrays and the whole frame is
        /// never materialized.
        /// </summary>
        public IReadOnlyList<BifrostMessage> SplitIntoChunks(BifrostMessage response)
        {
            var envelope = response.EncodeEnvelope();
            var payload = response.Payload;
            var serializedLength = envelope.Length + payload.Length;
            var chunkCount = (int)(((long)serializedLength + _chunkThreshold - 1) / _chunkThreshold);
            var chunks = new List<BifrostMessage>(chunkCount);

            for (var i = 0; i < chunkCount; i++)
            {
                var offset = i * _chunkThreshold;
                var chunkData = new byte[Math.Min(_chunkThreshold, serializedLength - offset)];
                CopySerializedRange(envelope, payload, offset, chunkData);

                chunks.Add(new BifrostMessage
                {
                    RequestId = response.RequestId,
                    Type = BifrostMessageType.Chunk,
                    Payload = chunkData,
                    ChunkSequence = (uint)i,
                    ChunkTotal = (uint)chunkCount,
                    ChunkOffset = (ulong)offset,
                    TotalBytes = (ulong)serializedLength,
                    ChunkChecksum = ComputeCrc32(chunkData),
                });
            }

            return chunks;
        }

        /// <summary>
        /// Fills <paramref name="destination"/> with the serialized message's bytes from
        /// <paramref name="offset"/>, reading the envelope and then the payload as one sequence.
        /// </summary>
        private static void CopySerializedRange(byte[] envelope, byte[] payload, int offset, byte[] destination)
        {
            var written = 0;
            if (offset < envelope.Length)
            {
                var fromEnvelope = Math.Min(envelope.Length - offset, destination.Length);
                envelope.AsSpan(offset, fromEnvelope).CopyTo(destination);
                written = fromEnvelope;
            }
            if (written == destination.Length)
                return;
            var payloadStart = offset + written - envelope.Length;
            payload.AsSpan(payloadStart, destination.Length - written).CopyTo(destination.AsSpan(written));
        }

        /// <summary>
        /// Starts one chunked transfer of <paramref name="chunks"/>. The transfer never reads
        /// the socket: the connection's single reader routes this request's ChunkAck/ChunkNack
        /// frames to <see cref="ChunkTransfer.AcknowledgeAsync"/>, so other requests on the same
        /// connection run and answer while the transfer waits for its acks.
        /// </summary>
        /// <param name="chunks">The chunks to send, from <see cref="SplitIntoChunks"/> or the buffer.</param>
        /// <param name="send">Sends one frame. The connection serializes every send through it.</param>
        public ChunkTransfer StartTransfer(
            IReadOnlyList<BifrostMessage> chunks,
            Func<BifrostMessage, CancellationToken, Task> send)
            => new(chunks, _ackWindow, _ackTimeout, _timeProvider, _maxRetransmitsPerSequence, _chunkBuffer, send);

        /// <summary>
        /// Computes a CRC32 checksum for the given data using System.IO.Hashing.
        /// </summary>
        public static uint ComputeCrc32(byte[] data)
        {
            return Crc32.HashToUInt32(data);
        }
    }

    /// <summary>
    /// Thrown when a single chunk sequence is retransmitted more times than the configured
    /// per-sequence cap during one transfer (a client that perpetually NACKs the same
    /// chunk). The middleware aborts the transfer and reports an error to the client.
    /// </summary>
    public sealed class ChunkRetransmitLimitExceededException : Exception
    {
        public uint RequestId { get; }
        public uint Sequence { get; }
        public int Limit { get; }

        public ChunkRetransmitLimitExceededException(uint requestId, uint sequence, int limit)
            : base($"Chunk {sequence} for request {requestId} exceeded the retransmission limit of {limit}; transfer aborted.")
        {
            RequestId = requestId;
            Sequence = sequence;
            Limit = limit;
        }
    }
}
