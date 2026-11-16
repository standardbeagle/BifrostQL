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
    /// as ChunkAck messages arrive (see <see cref="ChunkTransfer"/>).
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
        /// Default maximum time to wait for a ChunkAck/ChunkNack once the ack window is
        /// full. A client that stops acknowledging (or never acknowledges) would otherwise
        /// wedge the connection forever inside the send loop.
        /// </summary>
        public static readonly TimeSpan DefaultAckTimeout = TimeSpan.FromSeconds(30);

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

        public ChunkSender(int chunkThreshold = DefaultChunkThreshold, int ackWindow = DefaultAckWindow)
            : this(chunkThreshold, ackWindow, null)
        {
        }

        public ChunkSender(
            int chunkThreshold,
            int ackWindow,
            ChunkBuffer? chunkBuffer,
            TimeSpan? ackTimeout = null,
            ILogger? logger = null,
            int maxRetransmitsPerSequence = DefaultMaxRetransmitsPerSequence)
        {
            if (chunkThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(chunkThreshold));
            if (ackWindow <= 0) throw new ArgumentOutOfRangeException(nameof(ackWindow));
            if (ackTimeout.HasValue && ackTimeout.Value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(ackTimeout));
            if (maxRetransmitsPerSequence <= 0) throw new ArgumentOutOfRangeException(nameof(maxRetransmitsPerSequence));
            _chunkThreshold = chunkThreshold;
            _ackWindow = ackWindow;
            _chunkBuffer = chunkBuffer;
            _ackTimeout = ackTimeout ?? DefaultAckTimeout;
            _maxRetransmitsPerSequence = maxRetransmitsPerSequence;
            _logger = logger;
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
        /// Serializes the message to bytes and splits those bytes into chunk messages.
        /// Each chunk carries a fragment of the full serialized message with its CRC32
        /// checksum, sequence number, byte offset, and total metadata.
        /// The receiver reassembles the fragments and deserializes via BifrostMessage.FromBytes.
        /// </summary>
        public IReadOnlyList<BifrostMessage> SplitIntoChunks(BifrostMessage response)
        {
            var serialized = response.ToBytes();
            var totalBytes = (ulong)serialized.Length;
            var chunkCount = (int)((serialized.Length + _chunkThreshold - 1) / _chunkThreshold);
            var chunks = new List<BifrostMessage>(chunkCount);

            for (var i = 0; i < chunkCount; i++)
            {
                var offset = i * _chunkThreshold;
                var length = Math.Min(_chunkThreshold, serialized.Length - offset);
                var chunkData = new byte[length];
                Buffer.BlockCopy(serialized, offset, chunkData, 0, length);

                chunks.Add(new BifrostMessage
                {
                    RequestId = response.RequestId,
                    Type = BifrostMessageType.Chunk,
                    Payload = chunkData,
                    ChunkSequence = (uint)i,
                    ChunkTotal = (uint)chunkCount,
                    ChunkOffset = (ulong)offset,
                    TotalBytes = totalBytes,
                    ChunkChecksum = ComputeCrc32(chunkData),
                });
            }

            return chunks;
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
            => new(chunks, _ackWindow, _ackTimeout, _maxRetransmitsPerSequence, _chunkBuffer, send);

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
