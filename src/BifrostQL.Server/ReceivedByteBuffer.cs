namespace BifrostQL.Server
{
    /// <summary>
    /// The body of one length-prefixed frame, grown as its bytes ARRIVE rather than allocated at
    /// its declared length. Every raw-wire front door (pgwire, RESP, LDAP) caps a frame's declared
    /// length, but a cap check followed by <c>new byte[declared]</c> still lets a pre-auth peer pin
    /// the whole cap per connection by sending a length prefix and stalling: 100 such sockets held
    /// ~100 MiB per front door. The checklist rule is that size caps count received bytes, never
    /// declared ones; this is the one place that rule is implemented for frame bodies.
    ///
    /// <para>The buffer starts at <see cref="InitialCapacity"/> (or the declared length, if
    /// smaller) and doubles, never past the declared length, so the memory held is at most twice
    /// what the peer has actually sent and the final array is exactly the declared size.</para>
    /// </summary>
    internal struct ReceivedByteBuffer
    {
        /// <summary>The allocation made before any body byte has arrived.</summary>
        internal const int InitialCapacity = 16 * 1024;

        private readonly int _length;
        private byte[] _bytes;
        private int _received;

        public ReceivedByteBuffer(int declaredLength)
        {
            if (declaredLength < 0) throw new ArgumentOutOfRangeException(nameof(declaredLength));
            _length = declaredLength;
            _bytes = declaredLength == 0 ? Array.Empty<byte>() : new byte[Math.Min(declaredLength, InitialCapacity)];
            _received = 0;
        }

        /// <summary>Bytes still owed by the peer.</summary>
        public readonly int Remaining => _length - _received;

        /// <summary>Current allocation, for tests asserting it tracks received bytes.</summary>
        internal readonly int Capacity => _bytes.Length;

        /// <summary>
        /// The free space to receive into next, growing the buffer first when it is full. Never
        /// longer than <see cref="Remaining"/>; empty once the frame is complete.
        /// </summary>
        public Memory<byte> FreeSpace()
        {
            if (_received == _bytes.Length && _received < _length)
                Array.Resize(ref _bytes, (int)Math.Min((long)_bytes.Length * 2, _length));
            return _bytes.AsMemory(_received, _bytes.Length - _received);
        }

        /// <summary>Records <paramref name="count"/> bytes written into <see cref="FreeSpace"/>.</summary>
        public void Advance(int count)
        {
            if (count < 0 || count > _bytes.Length - _received) throw new ArgumentOutOfRangeException(nameof(count));
            _received += count;
        }

        /// <summary>The completed body. Only valid once <see cref="Remaining"/> is zero.</summary>
        public readonly byte[] ToArray()
        {
            if (_received != _length)
                throw new InvalidOperationException("the frame body is not complete.");
            return _bytes;
        }

        /// <summary>
        /// Reads exactly <paramref name="declaredLength"/> bytes from <paramref name="stream"/>,
        /// allocating as they arrive. Returns null when the stream ends first, so each front door
        /// raises its own protocol exception for a short frame.
        /// </summary>
        public static async Task<byte[]?> ReadAsync(Stream stream, int declaredLength, CancellationToken ct)
        {
            var body = new ReceivedByteBuffer(declaredLength);
            while (body.Remaining > 0)
            {
                var read = await stream.ReadAsync(body.FreeSpace(), ct).ConfigureAwait(false);
                if (read == 0)
                    return null;
                body.Advance(read);
            }
            return body.ToArray();
        }
    }
}
