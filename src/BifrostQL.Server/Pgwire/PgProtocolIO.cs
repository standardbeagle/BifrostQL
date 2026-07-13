using System.Buffers.Binary;
using System.Text;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>A decoded typed frontend (client → server) message: its type byte and body.</summary>
    internal readonly record struct PgFrontendMessage(byte Type, byte[] Body);

    /// <summary>
    /// Length-prefixed framing for the PostgreSQL v3 wire protocol over a byte
    /// <see cref="Stream"/>. Reads/writes are stream-oriented so the same code drives
    /// the raw TCP connection and, after the SSLRequest upgrade, the wrapped
    /// <see cref="System.Net.Security.SslStream"/> — the framing never changes, only
    /// the underlying stream does.
    ///
    /// <para>All integers are big-endian; a length field always counts itself. A
    /// hard <see cref="MaxMessageLength"/> bounds every frame so a malformed or hostile
    /// length prefix cannot drive an unbounded allocation during the unauthenticated
    /// handshake (fail fast, never trust the wire).</para>
    /// </summary>
    internal static class PgProtocolIO
    {
        /// <summary>Upper bound on any single handshake-phase frame body (1 MiB).</summary>
        public const int MaxMessageLength = 1 << 20;

        /// <summary>
        /// Reads an untyped startup-phase packet (<c>[Int32 len][Int32 code][rest…]</c>)
        /// used for SSLRequest, GSSENCRequest, CancelRequest and the StartupMessage.
        /// Returns the leading code/version int and the remaining body bytes.
        /// </summary>
        public static async Task<(int Code, byte[] Payload)> ReadStartupPacketAsync(Stream stream, CancellationToken ct)
        {
            var length = await ReadInt32Async(stream, ct);
            // length counts the 4 length bytes + the 4 code bytes at minimum.
            if (length < 8 || length > MaxMessageLength)
                throw new PgProtocolException($"Invalid startup packet length {length}.");

            var body = new byte[length - 4];
            await stream.ReadExactlyAsync(body, ct);
            var code = BinaryPrimitives.ReadInt32BigEndian(body);
            return (code, body[4..]);
        }

        /// <summary>
        /// Reads one typed frontend message (<c>[Byte type][Int32 len][body]</c>).
        /// </summary>
        public static async Task<PgFrontendMessage> ReadMessageAsync(Stream stream, CancellationToken ct)
        {
            var typeByte = new byte[1];
            await stream.ReadExactlyAsync(typeByte, ct);
            var length = await ReadInt32Async(stream, ct);
            if (length < 4 || length > MaxMessageLength)
                throw new PgProtocolException($"Invalid message length {length} for type '{(char)typeByte[0]}'.");

            var body = new byte[length - 4];
            if (body.Length > 0)
                await stream.ReadExactlyAsync(body, ct);
            return new PgFrontendMessage(typeByte[0], body);
        }

        /// <summary>Writes a typed backend message (<c>[type][Int32 len][body]</c>) and flushes.</summary>
        public static async Task WriteMessageAsync(Stream stream, byte type, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            var frame = new byte[1 + 4 + body.Length];
            frame[0] = type;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), 4 + body.Length);
            body.Span.CopyTo(frame.AsSpan(5));
            await stream.WriteAsync(frame, ct);
            await stream.FlushAsync(ct);
        }

        /// <summary>Writes a single raw byte (the 'S'/'N' answer to SSLRequest) and flushes.</summary>
        public static async Task WriteRawByteAsync(Stream stream, byte value, CancellationToken ct)
        {
            await stream.WriteAsync(new[] { value }, ct);
            await stream.FlushAsync(ct);
        }

        /// <summary>
        /// Parses a StartupMessage parameter body (the bytes after the protocol version):
        /// null-terminated <c>key\0value\0…\0</c> pairs ending in a final <c>\0</c>.
        /// </summary>
        public static IReadOnlyDictionary<string, string> ParseStartupParameters(ReadOnlySpan<byte> body)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            var offset = 0;
            while (offset < body.Length)
            {
                var key = ReadCString(body, ref offset);
                if (key.Length == 0)
                    break; // trailing terminator
                var value = ReadCString(body, ref offset);
                parameters[key] = value;
            }
            return parameters;
        }

        private static string ReadCString(ReadOnlySpan<byte> body, ref int offset)
        {
            var start = offset;
            while (offset < body.Length && body[offset] != 0)
                offset++;
            var text = Encoding.UTF8.GetString(body[start..offset]);
            if (offset < body.Length) offset++; // skip terminator
            return text;
        }

        private static async Task<int> ReadInt32Async(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[4];
            await stream.ReadExactlyAsync(buffer, ct);
            return BinaryPrimitives.ReadInt32BigEndian(buffer);
        }
    }

    /// <summary>
    /// The client violated the wire framing or protocol (bad length, truncated packet,
    /// malformed handshake message). Base type for the specific handshake-phase protocol
    /// faults so the connection handler can catch them in one place and answer with a
    /// protocol_violation ErrorResponse instead of leaking to Kestrel as unhandled.
    /// </summary>
    internal class PgProtocolException : Exception
    {
        public PgProtocolException(string message) : base(message) { }
    }
}
