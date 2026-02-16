using System.IO.Hashing;
using System.Net.WebSockets;

namespace BifrostQL.Server
{
    /// <summary>
    /// Splits large BifrostMessages into chunked frames and sends them over a WebSocket
    /// with backpressure control. The full message is first serialized to bytes, then
    /// those bytes are split into chunk-sized fragments. The receiver reassembles the
    /// fragments and deserializes to recover the original message.
    ///
    /// The server pauses sending when unacknowledged chunks exceed the configured
    /// window size, resuming when ChunkAck messages are received.
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

        private readonly int _chunkThreshold;
        private readonly int _ackWindow;
        private readonly ChunkBuffer? _chunkBuffer;

        public ChunkSender(int chunkThreshold = DefaultChunkThreshold, int ackWindow = DefaultAckWindow)
            : this(chunkThreshold, ackWindow, null)
        {
        }

        public ChunkSender(int chunkThreshold, int ackWindow, ChunkBuffer? chunkBuffer)
        {
            if (chunkThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(chunkThreshold));
            if (ackWindow <= 0) throw new ArgumentOutOfRangeException(nameof(ackWindow));
            _chunkThreshold = chunkThreshold;
            _ackWindow = ackWindow;
            _chunkBuffer = chunkBuffer;
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
        /// Sends chunked messages over a WebSocket with backpressure. The sender tracks
        /// unacknowledged chunks and pauses when the window is full, waiting for ChunkAck
        /// messages from the client before continuing. If a ChunkBuffer is configured,
        /// chunks are stored for potential retransmission on reconnect or checksum mismatch.
        /// </summary>
        /// <param name="webSocket">The WebSocket connection.</param>
        /// <param name="response">The response to send (will be chunked if above threshold).</param>
        /// <param name="receiveBuffer">Shared buffer for reading ACK messages.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task SendChunkedAsync(
            WebSocket webSocket,
            BifrostMessage response,
            byte[] receiveBuffer,
            CancellationToken cancellationToken)
        {
            var chunks = SplitIntoChunks(response);
            await SendChunksAsync(webSocket, chunks, 0, receiveBuffer, cancellationToken);
        }

        /// <summary>
        /// Sends chunks starting from a given index, storing each in the chunk buffer
        /// if one is configured. Used both for initial sends and resume retransmissions.
        /// </summary>
        internal async Task SendChunksAsync(
            WebSocket webSocket,
            IReadOnlyList<BifrostMessage> chunks,
            int startIndex,
            byte[] receiveBuffer,
            CancellationToken cancellationToken)
        {
            var unacked = 0;

            for (var i = startIndex; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                _chunkBuffer?.Add(chunk.RequestId, chunk.ChunkSequence, chunk);

                var chunkBytes = chunk.ToBytes();
                await webSocket.SendAsync(
                    new ArraySegment<byte>(chunkBytes),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cancellationToken);

                unacked++;

                // Drain available ACKs without blocking when under the window limit
                while (unacked > 0 && webSocket.State == WebSocketState.Open)
                {
                    if (unacked >= _ackWindow)
                    {
                        // Window full: must wait for at least one ACK or NACK
                        var result = await WaitForAckAsync(webSocket, receiveBuffer, cancellationToken);
                        if (result.acksReceived > 0)
                            unacked -= result.acksReceived;
                        if (result.nackSequence.HasValue)
                        {
                            await RetransmitChunkAsync(webSocket, chunk.RequestId, result.nackSequence.Value, cancellationToken);
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Waits for a single ACK or NACK message from the client.
        /// Returns the number of ACKs received and optionally a NACK sequence for retransmission.
        /// </summary>
        private static async Task<(int acksReceived, uint? nackSequence)> WaitForAckAsync(
            WebSocket webSocket,
            byte[] receiveBuffer,
            CancellationToken cancellationToken)
        {
            var result = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(receiveBuffer),
                cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);

            if (result.MessageType != WebSocketMessageType.Binary || result.Count == 0)
                return (0, null);

            var ackData = new byte[result.Count];
            Buffer.BlockCopy(receiveBuffer, 0, ackData, 0, result.Count);
            var ackMsg = BifrostMessage.FromBytes(ackData);

            if (ackMsg.Type == BifrostMessageType.ChunkAck)
                return (1, null);

            if (ackMsg.Type == BifrostMessageType.ChunkNack)
                return (0, ackMsg.ChunkSequence);

            return (0, null);
        }

        /// <summary>
        /// Retransmits a specific chunk from the buffer. If the buffer does not contain
        /// the chunk (expired or no buffer configured), this is a no-op.
        /// </summary>
        private async Task RetransmitChunkAsync(
            WebSocket webSocket,
            uint requestId,
            uint sequence,
            CancellationToken cancellationToken)
        {
            var chunk = _chunkBuffer?.TryGet(requestId, sequence);
            if (chunk == null)
                return;

            var chunkBytes = chunk.ToBytes();
            await webSocket.SendAsync(
                new ArraySegment<byte>(chunkBytes),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }

        /// <summary>
        /// Computes a CRC32 checksum for the given data using System.IO.Hashing.
        /// </summary>
        public static uint ComputeCrc32(byte[] data)
        {
            return Crc32.HashToUInt32(data);
        }
    }
}
