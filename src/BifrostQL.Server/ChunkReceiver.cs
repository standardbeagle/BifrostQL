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
        /// <summary>
        /// Default maximum reassembled payload size (64 MB). A chunk declaring a larger
        /// TotalBytes is rejected before any allocation happens, bounding the memory a
        /// hostile client can force the server to allocate from a single frame.
        /// </summary>
        public const int DefaultMaxReassemblyBytes = 64 * 1024 * 1024;

        /// <summary>
        /// Maximum declared chunk count per transfer. Bounds the tracking array allocation
        /// (ChunkTotal is client-controlled and would otherwise allow a ~4 GB bool[]).
        /// 65536 chunks covers the 64 MB payload cap at a 1 KB minimum chunk size.
        /// </summary>
        public const int MaxChunkCount = 65536;

        /// <summary>
        /// Default maximum number of concurrently pending reassembly sessions.
        /// </summary>
        public const int DefaultMaxPendingReassemblies = 16;

        /// <summary>
        /// Default idle time-to-live for a pending reassembly session. Sessions that
        /// receive no chunks within this window are evicted lazily on the next AddChunk.
        /// </summary>
        public static readonly TimeSpan DefaultReassemblyTtl = TimeSpan.FromMinutes(2);

        private readonly Dictionary<uint, ReassemblyState> _pending = new();
        private readonly int _maxReassemblyBytes;
        private readonly int _maxPendingReassemblies;
        private readonly TimeSpan _reassemblyTtl;

        public ChunkReceiver()
            : this(DefaultMaxReassemblyBytes, DefaultMaxPendingReassemblies, DefaultReassemblyTtl)
        {
        }

        public ChunkReceiver(int maxReassemblyBytes, int maxPendingReassemblies, TimeSpan reassemblyTtl)
        {
            if (maxReassemblyBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxReassemblyBytes));
            if (maxPendingReassemblies <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPendingReassemblies));
            if (reassemblyTtl <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(reassemblyTtl));
            _maxReassemblyBytes = maxReassemblyBytes;
            _maxPendingReassemblies = maxPendingReassemblies;
            _reassemblyTtl = reassemblyTtl;
        }

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
                // Validate client-declared sizes before any allocation: TotalBytes and
                // ChunkTotal come straight off the wire and would otherwise let a single
                // hostile frame force multi-gigabyte allocations.
                if (chunk.TotalBytes > (ulong)_maxReassemblyBytes)
                {
                    throw new InvalidOperationException(
                        $"Payload too large for reassembly: {chunk.TotalBytes} bytes exceeds the {_maxReassemblyBytes}-byte limit");
                }

                if (chunk.ChunkTotal > MaxChunkCount)
                {
                    throw new InvalidOperationException(
                        $"Chunk total {chunk.ChunkTotal} exceeds the {MaxChunkCount}-chunk limit");
                }

                EvictStalePending();
                if (_pending.Count >= _maxPendingReassemblies)
                {
                    throw new InvalidOperationException(
                        $"Too many pending chunked transfers: limit is {_maxPendingReassemblies}");
                }

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
        /// Evicts pending reassembly sessions that have received no chunks within the TTL.
        /// Called lazily when a new session is about to be created, so abandoned transfers
        /// cannot pin their buffers (or exhaust the pending-session limit) indefinitely.
        /// </summary>
        private void EvictStalePending()
        {
            List<uint>? stale = null;
            foreach (var kvp in _pending)
            {
                if (kvp.Value.IsIdleLongerThan(_reassemblyTtl))
                    (stale ??= new List<uint>()).Add(kvp.Key);
            }

            if (stale == null)
                return;

            foreach (var requestId in stale)
                _pending.Remove(requestId);
        }

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
            private long _lastActivityTicks;

            public ReassemblyState(uint totalChunks, ulong totalBytes)
            {
                // Size limits are validated by the caller (AddChunk) before construction,
                // so both allocations here are bounded by the configured caps.
                _buffer = new byte[(int)totalBytes];
                _received = new bool[totalChunks];
                _lastActivityTicks = Environment.TickCount64;
            }

            public bool IsComplete => _receivedCount == _received.Length;

            public bool IsIdleLongerThan(TimeSpan ttl)
                => Environment.TickCount64 - _lastActivityTicks >= (long)ttl.TotalMilliseconds;

            public void AddChunk(uint sequence, ulong offset, byte[] data)
            {
                _lastActivityTicks = Environment.TickCount64;

                if (sequence >= _received.Length)
                    throw new InvalidOperationException($"Chunk sequence {sequence} exceeds total {_received.Length}");

                // Bounds-check the offset before copying. A hostile or corrupt chunk can
                // declare an offset/length that falls outside the declared payload; reject
                // it with a clear error rather than letting Buffer.BlockCopy throw.
                // Compared without summing offset+length so a near-ulong.MaxValue offset
                // cannot wrap past the guard (which would then truncate to a negative int).
                if (offset > (ulong)_buffer.Length || (ulong)data.Length > (ulong)_buffer.Length - offset)
                    throw new InvalidOperationException(
                        $"Chunk at offset {offset} with length {data.Length} exceeds payload size {_buffer.Length}");

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
