namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// The client violated BER/LDAP framing (bad tag, indefinite length, a definite length that
    /// overruns its container, an out-of-range integer, a length past the message cap). This is
    /// the base type the connection loop's catch clause filters on, so a malformed message from
    /// an UNAUTHENTICATED peer becomes a clean Notice-of-Disconnection + close, never an
    /// unhandled throw that escapes to Kestrel (protocol-adapter-security invariant 1). Typed
    /// value decoding here raises this type rather than a raw BCL parse exception, but the loop's
    /// catch also widens to the <c>FormatException/OverflowException/ArgumentException</c> family
    /// as defense in depth (invariant 5).
    /// </summary>
    internal sealed class LdapProtocolException : Exception
    {
        public LdapProtocolException(string message) : base(message) { }
    }

    /// <summary>One decoded BER element: its tag byte and the bounds of its content in the buffer.</summary>
    internal readonly struct BerElement
    {
        public BerElement(byte tag, int contentStart, int contentLength)
        {
            Tag = tag;
            ContentStart = contentStart;
            ContentLength = contentLength;
        }

        public byte Tag { get; }
        public int ContentStart { get; }
        public int ContentLength { get; }
        public int ContentEnd => ContentStart + ContentLength;

        /// <summary>True for a constructed tag (bit 6 set) — its content is itself BER elements.</summary>
        public bool IsConstructed => (Tag & 0x20) != 0;
    }

    /// <summary>
    /// A cursor over an in-memory BER buffer bounded to <c>[Position, End)</c>. A whole LDAP
    /// message is read off the socket into a buffer whose size the definite-length cap already
    /// bounds (see <see cref="LdapMessageReader"/>), so parsing is a pure, synchronous walk of
    /// that buffer — no per-element socket read, no unbounded allocation. Every declared length
    /// is validated to fit inside the current container, so a lying inner length overruns nothing
    /// and a truncated element is a clean protocol error rather than an out-of-bounds read.
    ///
    /// <para><b>Nesting.</b> Constructed content is parsed through <see cref="Child"/>, a
    /// sub-cursor over just that element's content. The BER structure is recursive (SEQUENCEs,
    /// SETs, and the filter tree), so any caller that descends MUST bound its own recursion
    /// depth — see the depth counter threaded through <see cref="LdapMessageReader"/>'s filter
    /// parse (protocol-adapter-security invariant 6): an unbounded recursive walk of a
    /// deeply-nested definite-length buffer overflows the physical stack, which is uncatchable
    /// and crashes the whole host process.</para>
    /// </summary>
    internal sealed class BerCursor
    {
        private readonly byte[] _buffer;
        private int _position;
        private readonly int _end;

        public BerCursor(byte[] buffer, int start, int end)
        {
            _buffer = buffer;
            _position = start;
            _end = end;
        }

        /// <summary>Whether any bytes remain to read in this cursor's bounds.</summary>
        public bool HasMore => _position < _end;

        /// <summary>The tag byte of the next element without consuming it, or -1 at end.</summary>
        public int PeekTag => _position < _end ? _buffer[_position] : -1;

        /// <summary>
        /// Reads the next element's tag + length, advances past the whole element, and returns
        /// its content bounds. Rejects a truncated header, an indefinite length, an over-wide
        /// length prefix, and a definite length that overruns the container.
        /// </summary>
        public BerElement ReadElement()
        {
            if (_position >= _end)
                throw new LdapProtocolException("unexpected end of BER data while reading a tag.");
            var tag = _buffer[_position++];
            var length = ReadLength();
            var contentStart = _position;
            if (length > _end - contentStart)
                throw new LdapProtocolException(
                    $"BER element length {length} overruns its container (only {_end - contentStart} bytes remain).");
            _position = contentStart + length;
            return new BerElement(tag, contentStart, length);
        }

        /// <summary>Reads the next element, requiring its tag to equal <paramref name="expected"/>.</summary>
        public BerElement ReadElement(byte expected)
        {
            var element = ReadElement();
            if (element.Tag != expected)
                throw new LdapProtocolException(
                    $"expected BER tag 0x{expected:X2} but found 0x{element.Tag:X2}.");
            return element;
        }

        /// <summary>A sub-cursor scoped to a constructed element's content.</summary>
        public BerCursor Child(BerElement element) => new(_buffer, element.ContentStart, element.ContentEnd);

        /// <summary>The raw content bytes of an element (copied out).</summary>
        public byte[] Content(BerElement element)
        {
            var slice = new byte[element.ContentLength];
            Array.Copy(_buffer, element.ContentStart, slice, 0, element.ContentLength);
            return slice;
        }

        /// <summary>Decodes an element's content as a UTF-8 string (LDAPString / LDAPDN / OID text).</summary>
        public string String(BerElement element)
            => System.Text.Encoding.UTF8.GetString(_buffer, element.ContentStart, element.ContentLength);

        /// <summary>
        /// Decodes an element's content as a BER two's-complement signed integer. Bounded to a
        /// 64-bit value: a definite but oversized (&gt;8-byte) integer is a clean protocol error,
        /// never a silent truncation — the boundary an unbounded integer decode would otherwise
        /// leak past the connection handler (protocol-adapter-security invariant 5).
        /// </summary>
        public long Integer(BerElement element)
        {
            if (element.ContentLength == 0)
                throw new LdapProtocolException("BER integer must have at least one content byte.");
            if (element.ContentLength > 8)
                throw new LdapProtocolException(
                    $"BER integer of {element.ContentLength} bytes exceeds the 64-bit bound.");
            var start = element.ContentStart;
            // Sign-extend from the most-significant byte's high bit.
            long value = (sbyte)_buffer[start];
            for (var i = 1; i < element.ContentLength; i++)
                value = (value << 8) | _buffer[start + i];
            return value;
        }

        /// <summary>
        /// Decodes an element's content as a BER Boolean (control criticality, typesOnly…). A Boolean
        /// carries exactly one content byte; any nonzero byte is true. Any other length is a clean
        /// protocol error, never an out-of-bounds read — the boundary an unchecked <c>Content(...)[0]</c>
        /// would leak past the connection handler's catch filter as an uncaught IndexOutOfRangeException
        /// on an unauthenticated peer's malformed control (protocol-adapter-security invariant 5).
        ///
        /// <para>The length is checked for EQUALITY, not merely for emptiness: LDAP mandates the DER
        /// encoding of BOOLEAN (RFC 4511 §5.1), so a multi-octet Boolean is a wire violation. Reading
        /// only its first octet would silently admit a second spelling of the same value — an
        /// ambiguity an unauthenticated peer chooses and any middlebox may read differently.</para>
        /// </summary>
        public bool Boolean(BerElement element)
        {
            if (element.ContentLength != 1)
                throw new LdapProtocolException("BER Boolean must have exactly one content byte.");
            return _buffer[element.ContentStart] != 0;
        }

        /// <summary>Decodes an integer element and range-checks it into an <see cref="int"/> (messageID, sizeLimit…).</summary>
        public int Int32(BerElement element)
        {
            var value = Integer(element);
            if (value < int.MinValue || value > int.MaxValue)
                throw new LdapProtocolException($"BER integer {value} is out of the 32-bit range.");
            return (int)value;
        }

        // Reads a definite BER length. Indefinite form (0x80) is rejected — LDAP forbids it.
        private int ReadLength()
        {
            if (_position >= _end)
                throw new LdapProtocolException("unexpected end of BER data while reading a length.");
            int first = _buffer[_position++];
            if (first < 0x80)
                return first; // short form
            if (first == 0x80)
                throw new LdapProtocolException("indefinite BER length is forbidden in LDAP.");
            var byteCount = first & 0x7F;
            if (byteCount > 4)
                throw new LdapProtocolException($"BER length prefix of {byteCount} bytes exceeds the 32-bit bound.");
            long length = 0;
            for (var i = 0; i < byteCount; i++)
            {
                if (_position >= _end)
                    throw new LdapProtocolException("unexpected end of BER data inside a long-form length.");
                length = (length << 8) | _buffer[_position++];
            }
            if (length > int.MaxValue)
                throw new LdapProtocolException($"BER length {length} exceeds the 32-bit bound.");
            return (int)length;
        }
    }
}
