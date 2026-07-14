using System.Globalization;
using System.Text;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// The client violated RESP framing (bad length, truncated frame, unknown type marker,
    /// oversized aggregate/bulk). Base type the connection loop's catch clause filters on so
    /// a malformed frame from an unauthenticated peer becomes a clean protocol-error reply +
    /// close, never an unhandled throw that escapes to Kestrel. Length/number parsing here
    /// uses <c>TryParse</c> rather than throwing BCL parse exceptions, but the loop's catch
    /// also widens to the <c>FormatException/OverflowException/ArgumentException</c> family
    /// (per the protocol-adapter wire-decode invariant) as defense in depth.
    /// </summary>
    internal class RespProtocolException : Exception
    {
        public RespProtocolException(string message) : base(message) { }
    }

    /// <summary>
    /// Streaming RESP frame decoder over a byte <see cref="Stream"/>. Buffers reads so the
    /// same instance drives a real socket (tests, production) frame after frame. A hard
    /// per-frame cap on bulk length, line length, aggregate element count and nesting depth
    /// bounds every allocation and every stack frame, so a hostile length prefix or a deeply
    /// nested aggregate on the UNAUTHENTICATED path cannot drive an unbounded allocation or an
    /// unbounded (uncatchable-<c>StackOverflowException</c>) recursion (mirrors the pgwire
    /// <c>MaxMessageLength</c> DoS guard). Every malformed input raises
    /// <see cref="RespProtocolException"/> — never an unhandled throw.
    /// </summary>
    internal sealed class RespReader
    {
        private readonly Stream _stream;
        private readonly int _maxBulkLength;
        private readonly int _maxElements;
        private readonly int _maxNestingDepth;
        private readonly byte[] _buffer = new byte[8192];
        private int _start;
        private int _end;

        public RespReader(Stream stream, int maxBulkLength, int maxElements, int maxNestingDepth)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _maxBulkLength = maxBulkLength;
            _maxElements = maxElements;
            _maxNestingDepth = maxNestingDepth;
        }

        /// <summary>
        /// Reads the next top-level RESP value, or <c>null</c> on a clean end-of-stream
        /// between frames (peer closed). A partial frame at EOF is a protocol violation.
        /// </summary>
        public async Task<RespValue?> ReadValueAsync(CancellationToken ct)
        {
            var marker = await ReadRawByteAsync(ct);
            if (marker < 0)
                return null; // clean EOF between frames
            return await ReadBodyAsync((byte)marker, depth: 0, ct);
        }

        private async Task<RespValue> ReadBodyAsync(byte marker, int depth, CancellationToken ct)
        {
            switch (marker)
            {
                case RespProtocol.SimpleString:
                    return new RespSimpleString(await ReadLineAsync(ct));
                case RespProtocol.Error:
                    return new RespError(await ReadLineAsync(ct));
                case RespProtocol.Integer:
                    return new RespInteger(ParseLong(await ReadLineAsync(ct)));
                case RespProtocol.BulkString:
                    return await ReadBulkStringAsync(ct);
                case RespProtocol.VerbatimString:
                    return await ReadVerbatimAsync(ct);
                case RespProtocol.Array:
                    return new RespArray(await ReadAggregateAsync(depth, ct, allowNull: true));
                case RespProtocol.Set:
                    return new RespSet(await ReadNonNullAggregateAsync(depth, ct, "set"));
                case RespProtocol.Push:
                    return new RespPush(await ReadNonNullAggregateAsync(depth, ct, "push"));
                case RespProtocol.Map:
                    return await ReadMapAsync(depth, ct);
                case RespProtocol.Null:
                    var body = await ReadLineAsync(ct);
                    if (body.Length != 0)
                        throw new RespProtocolException("RESP null frame must have an empty body.");
                    return RespNull.Instance;
                case RespProtocol.Boolean:
                    return new RespBoolean(ParseBool(await ReadLineAsync(ct)));
                case RespProtocol.Double:
                    return new RespDouble(ParseDouble(await ReadLineAsync(ct)));
                case RespProtocol.BigNumber:
                    return new RespBigNumber(ParseBigNumber(await ReadLineAsync(ct)));
                default:
                    throw new RespProtocolException($"unknown RESP type byte 0x{marker:X2}.");
            }
        }

        private async Task<RespValue> ReadBulkStringAsync(CancellationToken ct)
        {
            var length = ParseInt(await ReadLineAsync(ct));
            if (length == RespProtocol.NullLength)
                return new RespBulkString(null);
            if (length < 0)
                throw new RespProtocolException($"invalid bulk-string length {length}.");
            if (length > _maxBulkLength)
                throw new RespProtocolException($"bulk-string length {length} exceeds cap {_maxBulkLength}.");
            var data = await ReadExactAsync(length, ct);
            await ExpectCrlfAsync(ct);
            return new RespBulkString(data);
        }

        private async Task<RespValue> ReadVerbatimAsync(CancellationToken ct)
        {
            var length = ParseInt(await ReadLineAsync(ct));
            if (length < 4)
                throw new RespProtocolException($"invalid verbatim-string length {length}.");
            if (length > _maxBulkLength)
                throw new RespProtocolException($"verbatim-string length {length} exceeds cap {_maxBulkLength}.");
            var data = await ReadExactAsync(length, ct);
            await ExpectCrlfAsync(ct);
            if (data[3] != (byte)':')
                throw new RespProtocolException("verbatim string missing 3-char format ':' separator.");
            var format = Encoding.ASCII.GetString(data, 0, 3);
            var content = Encoding.UTF8.GetString(data, 4, length - 4);
            return new RespVerbatimString(format, content);
        }

        private async Task<RespValue> ReadMapAsync(int depth, CancellationToken ct)
        {
            var count = ParseInt(await ReadLineAsync(ct));
            if (count < 0)
                throw new RespProtocolException($"invalid map length {count}.");
            if (count > _maxElements)
                throw new RespProtocolException($"map length {count} exceeds cap {_maxElements}.");
            // Guard the physical stack BEFORE descending into entries: a map whose entries are
            // themselves maps/arrays would recurse one frame per level, and with buffered socket
            // data the awaits complete synchronously so the stack grows unbounded → uncatchable
            // StackOverflowException. Reject past the cap as a clean protocol error instead.
            var childDepth = NextDepth(depth);
            // Grow incrementally rather than pre-allocating `count` entries: a tiny lying prefix
            // (e.g. `%1000000\r\n`) must not force a proportional up-front allocation; each entry
            // is materialized only as its bytes actually arrive, and a truncated stream throws
            // on EOF long before the declared count is reached.
            var entries = new List<KeyValuePair<RespValue, RespValue>>();
            for (var i = 0; i < count; i++)
            {
                var key = await ReadRequiredValueAsync(childDepth, ct, "map key");
                var value = await ReadRequiredValueAsync(childDepth, ct, "map value");
                entries.Add(new KeyValuePair<RespValue, RespValue>(key, value));
            }
            return new RespMap(entries);
        }

        private async Task<IReadOnlyList<RespValue>> ReadNonNullAggregateAsync(int depth, CancellationToken ct, string kind)
            => await ReadAggregateAsync(depth, ct, allowNull: false)
               ?? throw new RespProtocolException($"{kind} aggregate cannot be null.");

        private async Task<IReadOnlyList<RespValue>?> ReadAggregateAsync(int depth, CancellationToken ct, bool allowNull)
        {
            var count = ParseInt(await ReadLineAsync(ct));
            if (count == RespProtocol.NullLength)
            {
                if (!allowNull)
                    throw new RespProtocolException("aggregate of this type cannot be null.");
                return null;
            }
            if (count < 0)
                throw new RespProtocolException($"invalid aggregate length {count}.");
            if (count > _maxElements)
                throw new RespProtocolException($"aggregate length {count} exceeds cap {_maxElements}.");
            // Guard the physical stack BEFORE descending into elements — see ReadMapAsync.
            var childDepth = NextDepth(depth);
            // Grow incrementally rather than pre-allocating `count` elements — see ReadMapAsync.
            var items = new List<RespValue>();
            for (var i = 0; i < count; i++)
                items.Add(await ReadRequiredValueAsync(childDepth, ct, "aggregate element"));
            return items;
        }

        /// <summary>
        /// The nesting level for the children of a value at <paramref name="depth"/>, refusing
        /// to descend past <see cref="_maxNestingDepth"/>. Called BEFORE the recursive read so a
        /// hostile deeply-nested aggregate never actually grows the stack past the cap.
        /// </summary>
        private int NextDepth(int depth)
        {
            if (depth >= _maxNestingDepth)
                throw new RespProtocolException($"nesting depth exceeds cap {_maxNestingDepth}.");
            return depth + 1;
        }

        private async Task<RespValue> ReadRequiredValueAsync(int depth, CancellationToken ct, string what)
        {
            var marker = await ReadRawByteAsync(ct);
            if (marker < 0)
                throw new RespProtocolException($"unexpected end of stream reading {what}.");
            return await ReadBodyAsync((byte)marker, depth, ct);
        }

        // ---- primitives ----

        private async Task<string> ReadLineAsync(CancellationToken ct)
        {
            using var line = new MemoryStream();
            while (true)
            {
                var b = await ReadRawByteAsync(ct);
                if (b < 0)
                    throw new RespProtocolException("unexpected end of stream while reading a line.");
                if (b == RespProtocol.Cr)
                {
                    var next = await ReadRawByteAsync(ct);
                    if (next != RespProtocol.Lf)
                        throw new RespProtocolException("expected LF after CR.");
                    return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
                }
                if (line.Length >= _maxBulkLength)
                    throw new RespProtocolException($"line exceeds maximum length {_maxBulkLength}.");
                line.WriteByte((byte)b);
            }
        }

        private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
        {
            var result = new byte[count];
            var got = 0;
            while (got < count)
            {
                if (_start >= _end && !await FillAsync(ct))
                    throw new RespProtocolException("unexpected end of stream while reading a bulk payload.");
                var take = Math.Min(count - got, _end - _start);
                Array.Copy(_buffer, _start, result, got, take);
                _start += take;
                got += take;
            }
            return result;
        }

        private async Task ExpectCrlfAsync(CancellationToken ct)
        {
            var cr = await ReadRawByteAsync(ct);
            var lf = await ReadRawByteAsync(ct);
            if (cr != RespProtocol.Cr || lf != RespProtocol.Lf)
                throw new RespProtocolException("expected CRLF terminator after a bulk payload.");
        }

        /// <summary>Returns the next buffered byte, or -1 on end-of-stream.</summary>
        private async ValueTask<int> ReadRawByteAsync(CancellationToken ct)
        {
            if (_start >= _end && !await FillAsync(ct))
                return -1;
            return _buffer[_start++];
        }

        private async ValueTask<bool> FillAsync(CancellationToken ct)
        {
            _start = 0;
            _end = await _stream.ReadAsync(_buffer, ct);
            return _end > 0;
        }

        private static long ParseLong(string text)
            => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new RespProtocolException($"invalid integer '{text}'.");

        private static int ParseInt(string text)
        {
            var value = ParseLong(text);
            if (value < int.MinValue || value > int.MaxValue)
                throw new RespProtocolException($"length '{text}' out of range.");
            return (int)value;
        }

        private static bool ParseBool(string text) => text switch
        {
            "t" => true,
            "f" => false,
            _ => throw new RespProtocolException($"invalid boolean '{text}'."),
        };

        private static double ParseDouble(string text) => text switch
        {
            "inf" or "+inf" or "infinity" => double.PositiveInfinity,
            "-inf" or "-infinity" => double.NegativeInfinity,
            "nan" => double.NaN,
            _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new RespProtocolException($"invalid double '{text}'."),
        };

        private static string ParseBigNumber(string text)
        {
            var body = text.StartsWith('-') || text.StartsWith('+') ? text[1..] : text;
            if (body.Length == 0 || !body.All(char.IsAsciiDigit))
                throw new RespProtocolException($"invalid big number '{text}'.");
            return text;
        }
    }
}
