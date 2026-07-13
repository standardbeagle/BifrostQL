using System.Buffers.Binary;
using System.Text;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Builds the backend (server → client) message bodies the startup + authentication
    /// phase sends. Each method returns only the message <i>body</i>; framing (type byte
    /// + length) is applied by <see cref="PgProtocolIO.WriteMessageAsync"/>.
    /// </summary>
    internal static class PgBackend
    {
        /// <summary>AuthenticationOk body: a single Int32 sub-code 0.</summary>
        public static byte[] AuthenticationOk() => Int32Body(PgWireProtocol.AuthOk);

        /// <summary>AuthenticationCleartextPassword body: Int32 sub-code 3.</summary>
        public static byte[] AuthenticationCleartextPassword() => Int32Body(PgWireProtocol.AuthCleartextPassword);

        /// <summary>
        /// AuthenticationSASL body: Int32 sub-code 10 followed by the offered mechanism
        /// names as null-terminated strings, terminated by a final null.
        /// </summary>
        public static byte[] AuthenticationSasl(params string[] mechanisms)
        {
            using var ms = new MemoryStream();
            WriteInt32(ms, PgWireProtocol.AuthSasl);
            foreach (var mechanism in mechanisms)
                WriteCString(ms, mechanism);
            ms.WriteByte(0); // list terminator
            return ms.ToArray();
        }

        /// <summary>AuthenticationSASLContinue body: Int32 sub-code 11 + SCRAM server-first bytes.</summary>
        public static byte[] AuthenticationSaslContinue(string serverFirstMessage)
            => Int32PrefixedUtf8(PgWireProtocol.AuthSaslContinue, serverFirstMessage);

        /// <summary>AuthenticationSASLFinal body: Int32 sub-code 12 + SCRAM server-final bytes.</summary>
        public static byte[] AuthenticationSaslFinal(string serverFinalMessage)
            => Int32PrefixedUtf8(PgWireProtocol.AuthSaslFinal, serverFinalMessage);

        /// <summary>
        /// ErrorResponse body: a sequence of typed fields, each <c>[Byte code][String\0]</c>,
        /// closed by a zero byte. Sent before closing a rejected connection so a client
        /// (psql, drivers) surfaces the real reason instead of a bare disconnect.
        /// </summary>
        public static byte[] ErrorResponse(string sqlState, string message)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(PgWireProtocol.ErrorFieldSeverity);
            WriteCString(ms, "FATAL");
            ms.WriteByte(PgWireProtocol.ErrorFieldSeverityNonLocalized);
            WriteCString(ms, "FATAL");
            ms.WriteByte(PgWireProtocol.ErrorFieldCode);
            WriteCString(ms, sqlState);
            ms.WriteByte(PgWireProtocol.ErrorFieldMessage);
            WriteCString(ms, message);
            ms.WriteByte(PgWireProtocol.ErrorFieldTerminator);
            return ms.ToArray();
        }

        private static byte[] Int32Body(int value)
        {
            var body = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(body, value);
            return body;
        }

        private static byte[] Int32PrefixedUtf8(int subCode, string text)
        {
            var textBytes = Encoding.UTF8.GetBytes(text);
            var body = new byte[4 + textBytes.Length];
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), subCode);
            textBytes.CopyTo(body.AsSpan(4));
            return body;
        }

        private static void WriteInt32(Stream stream, int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            stream.Write(buffer);
        }

        private static void WriteCString(Stream stream, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes);
            stream.WriteByte(0);
        }
    }
}
