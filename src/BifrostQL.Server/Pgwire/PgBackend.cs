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
        ///
        /// <para><paramref name="severity"/> selects the client's reaction: a handshake
        /// rejection uses <see cref="PgWireProtocol.SeverityFatal"/> (the connection
        /// closes), a query-phase error uses <see cref="PgWireProtocol.SeverityError"/>
        /// (the session stays usable for the next query — autocommit resilience).</para>
        /// </summary>
        public static byte[] ErrorResponse(string sqlState, string message, string severity = PgWireProtocol.SeverityFatal)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(PgWireProtocol.ErrorFieldSeverity);
            WriteCString(ms, severity);
            ms.WriteByte(PgWireProtocol.ErrorFieldSeverityNonLocalized);
            WriteCString(ms, severity);
            ms.WriteByte(PgWireProtocol.ErrorFieldCode);
            WriteCString(ms, sqlState);
            ms.WriteByte(PgWireProtocol.ErrorFieldMessage);
            WriteCString(ms, message);
            ms.WriteByte(PgWireProtocol.ErrorFieldTerminator);
            return ms.ToArray();
        }

        /// <summary>
        /// RowDescription body: Int16 field count, then per field —
        /// name C-string, table OID (Int32, 0 = not a real table column),
        /// column attribute number (Int16, 0), type OID (Int32), type length
        /// (Int16, -1 = variable), type modifier (Int32, -1 = none), and format
        /// code (Int16, 0 = text). Slice 2 emits text format for every column.
        /// </summary>
        public static byte[] RowDescription(IReadOnlyList<PgResultColumn> columns)
        {
            using var ms = new MemoryStream();
            WriteInt16(ms, (short)columns.Count);
            foreach (var column in columns)
            {
                var type = PgTypeMap.Map(column.DataType);
                WriteCString(ms, column.Name);
                WriteInt32(ms, 0);                          // table OID: none (adapter is not a base table)
                WriteInt16(ms, 0);                          // column attribute number: none
                WriteInt32(ms, type.Oid);
                WriteInt16(ms, type.TypeLength);
                WriteInt32(ms, PgTypeMap.NoTypeModifier);
                WriteInt16(ms, PgWireProtocol.FormatText);
            }
            return ms.ToArray();
        }

        /// <summary>
        /// DataRow body: Int16 column count, then per column an Int32 byte length
        /// followed by that many UTF-8 bytes. A SQL NULL is length
        /// <see cref="PgWireProtocol.NullValueLength"/> (-1) with no bytes —
        /// distinct from an empty string (length 0).
        /// </summary>
        public static byte[] DataRow(IReadOnlyList<string?> textValues)
        {
            using var ms = new MemoryStream();
            WriteInt16(ms, (short)textValues.Count);
            foreach (var text in textValues)
            {
                if (text is null)
                {
                    WriteInt32(ms, PgWireProtocol.NullValueLength);
                    continue;
                }
                var bytes = Encoding.UTF8.GetBytes(text);
                WriteInt32(ms, bytes.Length);
                ms.Write(bytes);
            }
            return ms.ToArray();
        }

        /// <summary>CommandComplete body: the command tag as a C-string, e.g. <c>"SELECT 3"</c>.</summary>
        public static byte[] CommandComplete(string tag)
        {
            using var ms = new MemoryStream();
            WriteCString(ms, tag);
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

        private static void WriteInt16(Stream stream, short value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(buffer, value);
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
