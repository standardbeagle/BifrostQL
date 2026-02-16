using System.IO.Hashing;

namespace BifrostQL.Server
{
    /// <summary>
    /// Reassembles chunked BifrostMessages into a complete message. Validates CRC32
    /// checksums on each chunk and tracks reassembly progress per request_id.
    /// Not thread-safe; intended for single-connection use within a WebSocket handler loop.
    ///
    /// Chunking works at two levels:
    /// - Server-to-client: chunks carry fragments of the data Payload field. The assembled
    ///   bytes are placed back into a Result message's Payload.
    /// - Client-to-server: chunks carry fragments of a full serialized BifrostMessage. The
    ///   assembled bytes are deserialized via BifrostMessage.FromBytes to recover the original
    ///   Query/Mutation message.
    /// </summary>
    public sealed class ChunkReceiver
    {
        private readonly Dictionary<uint, ReassemblyState> _pending = new();

        /// <summary>
        /// Processes an incoming chunk message. Returns the assembled raw bytes when all
        /// chunks have been received and validated, or null if more chunks are expected.
        /// Throws if a checksum mismatch is detected.
        /// The caller is responsible for interpreting the assembled bytes (e.g., deserializing
        /// as a BifrostMessage for client-to-server chunking, or using as a Result payload
        /// for server-to-client chunking).
        /// </summary>
        /// <param name="chunk">The incoming Chunk message.</param>
        /// <returns>The assembled raw bytes, or null if more chunks are pending.</returns>
        public byte[]? AddChunk(BifrostMessage chunk)
        {
            if (chunk.Type != BifrostMessageType.Chunk)
                throw new ArgumentException("Message is not a Chunk type", nameof(chunk));

            var actualCrc = Crc32.HashToUInt32(chunk.Payload);
            if (actualCrc != chunk.ChunkChecksum)
            {
                throw new InvalidOperationException(
                    $"CRC32 mismatch on chunk {chunk.ChunkSequence} for request {chunk.RequestId}: " +
                    $"expected {chunk.ChunkChecksum:X8}, got {actualCrc:X8}");
            }

            if (!_pending.TryGetValue(chunk.RequestId, out var state))
            {
                state = new ReassemblyState(chunk.ChunkTotal, chunk.TotalBytes);
                _pending[chunk.RequestId] = state;
            }

            state.AddChunk(chunk.ChunkSequence, chunk.ChunkOffset, chunk.Payload);

            if (!state.IsComplete)
                return null;

            _pending.Remove(chunk.RequestId);
            return state.GetAssembledPayload();
        }

        /// <summary>
        /// Returns the number of active reassembly sessions (for diagnostics).
        /// </summary>
        public int PendingCount => _pending.Count;

        /// <summary>
        /// Creates a ChunkAck message for acknowledging receipt of a chunk.
        /// </summary>
        public static BifrostMessage CreateAck(uint requestId, uint sequence)
        {
            return new BifrostMessage
            {
                RequestId = requestId,
                Type = BifrostMessageType.ChunkAck,
                ChunkSequence = sequence,
            };
        }

        private sealed class ReassemblyState
        {
            private readonly byte[] _buffer;
            private readonly bool[] _received;
            private int _receivedCount;

            public ReassemblyState(uint totalChunks, ulong totalBytes)
            {
                if (totalBytes > int.MaxValue)
                    throw new InvalidOperationException($"Payload too large for reassembly: {totalBytes} bytes");

                _buffer = new byte[(int)totalBytes];
                _received = new bool[totalChunks];
            }

            public bool IsComplete => _receivedCount == _received.Length;

            public void AddChunk(uint sequence, ulong offset, byte[] data)
            {
                if (sequence >= _received.Length)
                    throw new InvalidOperationException($"Chunk sequence {sequence} exceeds total {_received.Length}");

                if (_received[sequence])
                    return; // Duplicate chunk; ignore

                Buffer.BlockCopy(data, 0, _buffer, (int)offset, data.Length);
                _received[sequence] = true;
                _receivedCount++;
            }

            public byte[] GetAssembledPayload()
            {
                return _buffer;
            }
        }
    }
}
