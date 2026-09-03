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

        /// <summary>
        /// Default cap on the TOTAL bytes across all concurrent reassembly sessions for one
        /// connection (128 MB). Without an aggregate cap, N concurrent sessions each declaring
        /// the per-session maximum could pin N × max bytes (e.g. 16 × 64 MB ≈ 1 GB) from only
        /// kilobytes of traffic. This bounds the whole connection, not just each session.
        /// </summary>
        public const int DefaultMaxTotalReassemblyBytes = 128 * 1024 * 1024;

        private readonly Dictionary<uint, ReassemblyState> _pending = new();
        private long _currentAllocatedBytes;
        private readonly int _maxReassemblyBytes;
        private readonly int _maxPendingReassemblies;
        private readonly TimeSpan _reassemblyTtl;
        private readonly long _maxTotalReassemblyBytes;
        private long _currentReassemblyBytes;

        public ChunkReceiver()
            : this(DefaultMaxReassemblyBytes, DefaultMaxPendingReassemblies, DefaultReassemblyTtl)
        {
        }

        public ChunkReceiver(
            int maxReassemblyBytes,
            int maxPendingReassemblies,
            TimeSpan reassemblyTtl,
            long maxTotalReassemblyBytes = 0)
        {
            if (maxReassemblyBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxReassemblyBytes));
            if (maxPendingReassemblies <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPendingReassemblies));
            if (reassemblyTtl <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(reassemblyTtl));
            if (maxTotalReassemblyBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(maxTotalReassemblyBytes));
            _maxReassemblyBytes = maxReassemblyBytes;
            _maxPendingReassemblies = maxPendingReassemblies;
            _reassemblyTtl = reassemblyTtl;
            // 0 means "derive from the per-session cap": the aggregate is at least one full
            // session and never below the per-session cap.
            _maxTotalReassemblyBytes = maxTotalReassemblyBytes > 0
                ? maxTotalReassemblyBytes
                : Math.Max(maxReassemblyBytes, DefaultMaxTotalReassemblyBytes);
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

                // Aggregate cap: bound TOTAL in-flight reassembly memory for this connection,
                // not just each individual session, so many concurrent sessions cannot sum to
                // a much larger allocation than any single session's limit.
                if (_currentReassemblyBytes + (long)chunk.TotalBytes > _maxTotalReassemblyBytes)
                {
                    throw new InvalidOperationException(
                        $"Aggregate in-flight reassembly memory would exceed the {_maxTotalReassemblyBytes}-byte per-connection limit");
                }

                state = new ReassemblyState(chunk.ChunkTotal, chunk.TotalBytes);
                _pending[chunk.RequestId] = state;
                _currentReassemblyBytes += (long)chunk.TotalBytes;
            }

            var before = state.ReceivedBytes;
            try
            {
                state.AddChunk(chunk.ChunkSequence, chunk.ChunkOffset, chunk.Payload);
            }
            finally
            {
                _currentAllocatedBytes += state.ReceivedBytes - before;
            }

            if (!state.IsComplete)
                return null;

            _pending.Remove(chunk.RequestId);
            _currentReassemblyBytes -= state.DeclaredBytes;
            _currentAllocatedBytes -= state.ReceivedBytes;
            return state.GetAssembledPayload();
        }

        /// <summary>
        /// Returns the number of active reassembly sessions (for diagnostics).
        /// </summary>
        public int PendingCount => _pending.Count;

        /// <summary>
        /// Bytes this receiver currently holds for pending transfers. Reassembly memory grows
        /// from the fragments that actually ARRIVED, never from the client-declared TotalBytes,
        /// so a peer cannot make the server commit memory it never sends: two 20-byte frames
        /// declaring 64 MB hold 40 bytes.
        /// </summary>
        public long AllocatedBytes => _currentAllocatedBytes;

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
            {
                if (_pending.TryGetValue(requestId, out var evicted))
                {
                    _currentReassemblyBytes -= evicted.DeclaredBytes;
                    _currentAllocatedBytes -= evicted.ReceivedBytes;
                }
                _pending.Remove(requestId);
            }
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

        /// <summary>
        /// One in-flight transfer. Holds the fragments that ARRIVED — never a buffer sized from
        /// the client-declared total — so the memory a peer can pin is the memory it actually
        /// sent. The declared total still bounds the transfer (offsets must fall inside it, and
        /// the delivered bytes must add up to it before anything is assembled), but it is a
        /// promise to be checked, not an allocation to be made.
        /// </summary>
        private sealed class ReassemblyState
        {
            private readonly Dictionary<uint, (ulong Offset, byte[] Data)> _chunks = new();
            private readonly uint _totalChunks;
            private long _lastActivityTicks;

            public ReassemblyState(uint totalChunks, ulong totalBytes)
            {
                // The declared sizes are validated by the caller (AddChunk) before construction;
                // nothing is allocated from either of them here.
                _totalChunks = totalChunks;
                _lastActivityTicks = Environment.TickCount64;
                DeclaredBytes = (long)totalBytes;
            }

            /// <summary>The declared payload size this session reserved (for the aggregate cap).</summary>
            public long DeclaredBytes { get; }

            /// <summary>Bytes actually delivered so far — the memory this session holds.</summary>
            public long ReceivedBytes { get; private set; }

            public bool IsComplete => _chunks.Count == _totalChunks;

            public bool IsIdleLongerThan(TimeSpan ttl)
                => Environment.TickCount64 - _lastActivityTicks >= (long)ttl.TotalMilliseconds;

            public void AddChunk(uint sequence, ulong offset, byte[] data)
            {
                _lastActivityTicks = Environment.TickCount64;

                if (sequence >= _totalChunks)
                    throw new InvalidOperationException($"Chunk sequence {sequence} exceeds total {_totalChunks}");

                // Bounds-check the offset against the DECLARED size before retaining anything.
                // A hostile or corrupt chunk can declare an offset/length outside the declared
                // payload; reject it with a clear error rather than letting the assembly copy
                // throw. Compared without summing offset+length so a near-ulong.MaxValue offset
                // cannot wrap past the guard (which would then truncate to a negative int).
                if (offset > (ulong)DeclaredBytes || (ulong)data.Length > (ulong)DeclaredBytes - offset)
                    throw new InvalidOperationException(
                        $"Chunk at offset {offset} with length {data.Length} exceeds payload size {DeclaredBytes}");

                if (_chunks.ContainsKey(sequence))
                    return; // Duplicate chunk; ignore

                // Retained bytes can never exceed the declared total: overlapping or repeated
                // fragments must not let a transfer hold more than it promised (the per-session
                // and aggregate caps are expressed against the declared total).
                if (ReceivedBytes + data.Length > DeclaredBytes)
                    throw new InvalidOperationException(
                        $"Delivered bytes exceed the {DeclaredBytes}-byte payload the transfer declared");

                _chunks[sequence] = (offset, data);
                ReceivedBytes += data.Length;
            }

            /// <summary>
            /// Materializes the payload once every declared chunk has arrived. The delivered
            /// bytes must add up to the declared total: a transfer that declares 64 MB and
            /// delivers 40 bytes is refused here rather than materialized at the declared size,
            /// which would reintroduce the pre-allocation this class exists to avoid.
            /// </summary>
            public byte[] GetAssembledPayload()
            {
                if (ReceivedBytes != DeclaredBytes)
                    throw new InvalidOperationException(
                        $"Transfer declared {DeclaredBytes} bytes but delivered {ReceivedBytes}");

                var buffer = new byte[DeclaredBytes];
                foreach (var (offset, data) in _chunks.Values)
                    Buffer.BlockCopy(data, 0, buffer, (int)offset, data.Length);
                return buffer;
            }
        }
    }
}
