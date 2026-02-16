using System.Collections.Concurrent;

namespace BifrostQL.Server
{
    /// <summary>
    /// Thread-safe server-side chunk buffer that retains sent chunks for retransmission
    /// on client reconnection. Each entry is keyed by request_id and tracks all chunks
    /// for that transfer along with an expiration time.
    ///
    /// Eviction runs lazily on access (Add/TryGet) to avoid background timer overhead.
    /// Completed transfers are removed when explicitly marked done or when they expire.
    /// </summary>
    public sealed class ChunkBuffer
    {
        /// <summary>
        /// Default time-to-live for chunk buffer entries (5 minutes).
        /// </summary>
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Maximum number of eviction candidates checked per lazy sweep to bound
        /// the cost of eviction on each access.
        /// </summary>
        private const int MaxEvictionBatch = 32;

        private readonly ConcurrentDictionary<uint, BufferEntry> _entries = new();
        private readonly TimeSpan _ttl;
        private long _lastEvictionTicks;

        public ChunkBuffer() : this(DefaultTtl) { }

        public ChunkBuffer(TimeSpan ttl)
        {
            if (ttl <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive");
            _ttl = ttl;
            _lastEvictionTicks = Environment.TickCount64;
        }

        /// <summary>
        /// Stores a chunk for a given request. Creates the buffer entry on first chunk.
        /// </summary>
        public void Add(uint requestId, uint sequence, BifrostMessage chunk)
        {
            var entry = _entries.GetOrAdd(requestId, _ => new BufferEntry(chunk.ChunkTotal, _ttl));
            entry.SetChunk(sequence, chunk);
            TryEvict();
        }

        /// <summary>
        /// Retrieves a specific chunk for retransmission. Returns null if the request
        /// or chunk is not in the buffer (expired or never stored).
        /// </summary>
        public BifrostMessage? TryGet(uint requestId, uint sequence)
        {
            TryEvict();
            if (!_entries.TryGetValue(requestId, out var entry))
                return null;
            if (entry.IsExpired)
            {
                _entries.TryRemove(requestId, out _);
                return null;
            }
            return entry.GetChunk(sequence);
        }

        /// <summary>
        /// Returns all chunks for a request starting after the given sequence number.
        /// Used for Resume: client sends last_sequence, server returns chunks from
        /// last_sequence + 1 onwards. If lastSequence is uint.MaxValue, returns all chunks.
        /// </summary>
        public IReadOnlyList<BifrostMessage> GetChunksAfter(uint requestId, uint lastSequence)
        {
            TryEvict();
            if (!_entries.TryGetValue(requestId, out var entry))
                return Array.Empty<BifrostMessage>();
            if (entry.IsExpired)
            {
                _entries.TryRemove(requestId, out _);
                return Array.Empty<BifrostMessage>();
            }
            return entry.GetChunksAfter(lastSequence);
        }

        /// <summary>
        /// Marks a transfer as complete and removes it from the buffer.
        /// </summary>
        public void Complete(uint requestId)
        {
            _entries.TryRemove(requestId, out _);
        }

        /// <summary>
        /// Returns the number of active buffer entries (for diagnostics).
        /// </summary>
        public int Count => _entries.Count;

        /// <summary>
        /// Returns whether a specific request has chunks in the buffer.
        /// </summary>
        public bool Contains(uint requestId)
        {
            if (!_entries.TryGetValue(requestId, out var entry))
                return false;
            if (entry.IsExpired)
            {
                _entries.TryRemove(requestId, out _);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Lazily evicts expired entries. Runs at most once per second to amortize cost.
        /// </summary>
        private void TryEvict()
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastEvictionTicks);
            if (now - last < 1000)
                return;

            if (Interlocked.CompareExchange(ref _lastEvictionTicks, now, last) != last)
                return;

            var evicted = 0;
            foreach (var kvp in _entries)
            {
                if (evicted >= MaxEvictionBatch)
                    break;
                if (kvp.Value.IsExpired)
                {
                    _entries.TryRemove(kvp.Key, out _);
                    evicted++;
                }
            }
        }

        private sealed class BufferEntry
        {
            private readonly BifrostMessage?[] _chunks;
            private readonly long _expiresAtTicks;

            public BufferEntry(uint totalChunks, TimeSpan ttl)
            {
                _chunks = new BifrostMessage?[totalChunks];
                _expiresAtTicks = Environment.TickCount64 + (long)ttl.TotalMilliseconds;
            }

            public bool IsExpired => Environment.TickCount64 >= _expiresAtTicks;

            public void SetChunk(uint sequence, BifrostMessage chunk)
            {
                if (sequence < (uint)_chunks.Length)
                    _chunks[sequence] = chunk;
            }

            public BifrostMessage? GetChunk(uint sequence)
            {
                if (sequence >= (uint)_chunks.Length)
                    return null;
                return _chunks[sequence];
            }

            public IReadOnlyList<BifrostMessage> GetChunksAfter(uint lastSequence)
            {
                var startIndex = lastSequence == uint.MaxValue ? 0 : (int)(lastSequence + 1);
                if (startIndex >= _chunks.Length)
                    return Array.Empty<BifrostMessage>();

                var result = new List<BifrostMessage>();
                for (var i = startIndex; i < _chunks.Length; i++)
                {
                    if (_chunks[i] != null)
                        result.Add(_chunks[i]!);
                }
                return result;
            }
        }
    }
}
