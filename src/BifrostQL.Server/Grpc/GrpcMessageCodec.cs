using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Encodes and decodes gRPC request/response messages ON THE WIRE directly from the slice-1/2
    /// <see cref="GrpcContract"/> — there are no compiled <c>.proto</c> stubs. The service/method
    /// set is generated from <c>DbModel</c> at runtime, so dispatch cannot rely on generated message
    /// classes; instead every field's number and proto wire type come from the contract's
    /// <see cref="GrpcField"/> metadata, and this codec reads/writes the protobuf wire format for
    /// exactly those fields via <see cref="CodedInputStream"/>/<see cref="CodedOutputStream"/>.
    ///
    /// <para>Because the same reconciled manifest pins every field number for both the full dispatch
    /// contract and each identity-filtered reflection projection, a client that discovered the schema
    /// through reflection encodes/decodes against the identical numbers this codec uses.</para>
    /// </summary>
    internal static class GrpcMessageCodec
    {
        // Protobuf wire types (Google.Protobuf.WireFormat is internal, so the constants are inlined).
        private const uint WireVarint = 0;
        private const uint WireFixed64 = 1;
        private const uint WireLengthDelimited = 2;
        private const uint WireFixed32 = 5;

        /// <summary>Encodes one row as its <c>&lt;Table&gt;Row</c> message bytes (schema/number order, NULLs omitted).</summary>
        public static byte[] EncodeRow(GrpcMessage rowMessage, IReadOnlyDictionary<string, object?> row)
        {
            using var buffer = new MemoryStream();
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                WriteRowFields(output, rowMessage, row);
            }
            return buffer.ToArray();
        }

        /// <summary>Encodes a <c>Get&lt;Table&gt;Response</c>: field 1 (<c>row</c>) is the nested row message.</summary>
        public static byte[] EncodeGetResponse(
            GrpcMessage rowMessage, IReadOnlyDictionary<string, object?> row)
        {
            var rowBytes = EncodeRow(rowMessage, row);
            using var buffer = new MemoryStream();
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                WriteLengthDelimited(output, fieldNumber: 1, rowBytes);
            }
            return buffer.ToArray();
        }

        /// <summary>Encodes a <c>List&lt;Table&gt;Response</c>: field 1 (<c>rows</c>) repeated nested row messages.</summary>
        public static byte[] EncodeListResponse(
            GrpcMessage rowMessage, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        {
            using var buffer = new MemoryStream();
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                foreach (var row in rows)
                    WriteLengthDelimited(output, fieldNumber: 1, EncodeRow(rowMessage, row));
            }
            return buffer.ToArray();
        }

        /// <summary>
        /// Decodes a <c>Get&lt;Table&gt;Request</c> to a field-name → value map. Only fields declared
        /// on <paramref name="requestMessage"/> are read (each by its pinned number/type); an unknown
        /// field is skipped, never interpreted. A wire value that does not match its declared type
        /// throws — the single-funnel mapper turns that into a clean INVALID_ARGUMENT.
        /// </summary>
        public static IReadOnlyDictionary<string, object?> DecodeRequest(
            GrpcMessage requestMessage, byte[] payload)
        {
            var byNumber = requestMessage.Fields.ToDictionary(f => f.Number);
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);

            var input = new CodedInputStream(payload);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                var fieldNumber = (int)(tag >> 3);
                if (!byNumber.TryGetValue(fieldNumber, out var field))
                {
                    input.SkipLastField();
                    continue;
                }

                try
                {
                    values[field.Name] = ReadScalar(input, field.Scalar!.Value);
                }
                catch (Exception ex) when (ex is not GrpcRequestException)
                {
                    // Surface only the request field name — the declared-type/decode detail is generic
                    // and carries no internal column/table/SQL text (invariant 3 / criterion 4).
                    throw GrpcRequestException.InvalidField(
                        field.Name, "Value could not be decoded for its declared type.");
                }
            }

            return values;
        }

        private static void WriteRowFields(
            CodedOutputStream output, GrpcMessage rowMessage, IReadOnlyDictionary<string, object?> row)
        {
            foreach (var field in rowMessage.Fields)
            {
                // A column the pipeline masked/omitted (or a SQL NULL) is simply absent — proto3
                // default presence. A denied column therefore never surfaces on the wire.
                if (!row.TryGetValue(field.Name, out var value) || value is null || value is DBNull)
                    continue;

                if (field.Scalar == GrpcScalarKind.Timestamp)
                {
                    WriteTag(output, field.Number, WireLengthDelimited);
                    output.WriteMessage(ToTimestamp(value));
                    continue;
                }

                WriteScalar(output, field.Number, field.Scalar!.Value, value);
            }
        }

        private static void WriteScalar(
            CodedOutputStream output, int number, GrpcScalarKind kind, object value)
        {
            switch (kind)
            {
                case GrpcScalarKind.Int32:
                    WriteTag(output, number, WireVarint);
                    output.WriteInt32(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                    break;
                case GrpcScalarKind.Int64:
                    WriteTag(output, number, WireVarint);
                    output.WriteInt64(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;
                case GrpcScalarKind.Bool:
                    WriteTag(output, number, WireVarint);
                    output.WriteBool(Convert.ToBoolean(value, CultureInfo.InvariantCulture));
                    break;
                case GrpcScalarKind.Double:
                    WriteTag(output, number, WireFixed64);
                    output.WriteDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    break;
                case GrpcScalarKind.Float:
                    WriteTag(output, number, WireFixed32);
                    output.WriteFloat(Convert.ToSingle(value, CultureInfo.InvariantCulture));
                    break;
                case GrpcScalarKind.Bytes:
                    WriteTag(output, number, WireLengthDelimited);
                    output.WriteBytes(UnsafeByteOperations.UnsafeWrap((byte[])value));
                    break;
                case GrpcScalarKind.String:
                default:
                    WriteTag(output, number, WireLengthDelimited);
                    output.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                    break;
            }
        }

        private static object? ReadScalar(CodedInputStream input, GrpcScalarKind kind) => kind switch
        {
            GrpcScalarKind.Int32 => input.ReadInt32(),
            GrpcScalarKind.Int64 => input.ReadInt64(),
            GrpcScalarKind.Bool => input.ReadBool(),
            GrpcScalarKind.Double => input.ReadDouble(),
            GrpcScalarKind.Float => input.ReadFloat(),
            GrpcScalarKind.Bytes => input.ReadBytes().ToByteArray(),
            GrpcScalarKind.Timestamp => ReadTimestamp(input),
            _ => input.ReadString(),
        };

        private static DateTime ReadTimestamp(CodedInputStream input)
        {
            var ts = new Timestamp();
            input.ReadMessage(ts);
            return ts.ToDateTime();
        }

        private static Timestamp ToTimestamp(object value) => value switch
        {
            DateTimeOffset dto => Timestamp.FromDateTimeOffset(dto),
            DateTime dt => Timestamp.FromDateTime(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => Timestamp.FromDateTime(DateTime.SpecifyKind(
                Convert.ToDateTime(value, CultureInfo.InvariantCulture), DateTimeKind.Utc)),
        };

        /// <summary>Writes a length-delimited (wire type 2) field carrying a raw pre-encoded payload.</summary>
        private static void WriteLengthDelimited(CodedOutputStream output, int fieldNumber, byte[] payload)
        {
            WriteTag(output, fieldNumber, WireLengthDelimited);
            // WriteBytes emits the length prefix + raw bytes — identical wire shape to a nested
            // message body, which is what a repeated/nested row field is.
            output.WriteBytes(UnsafeByteOperations.UnsafeWrap(payload));
        }

        /// <summary>
        /// Writes a field tag as its raw varint bytes. Generated protobuf code uses
        /// <c>WriteRawTag</c> for exactly this; the tag = (number &lt;&lt; 3) | wireType encoded as a
        /// varint (1 byte for numbers &lt; 16, more for larger).
        /// </summary>
        private static void WriteTag(CodedOutputStream output, int fieldNumber, uint wireType)
        {
            var tag = (uint)(fieldNumber << 3) | wireType;
            Span<byte> bytes = stackalloc byte[5];
            var length = 0;
            while (tag >= 0x80)
            {
                bytes[length++] = (byte)(tag | 0x80);
                tag >>= 7;
            }
            bytes[length++] = (byte)tag;

            switch (length)
            {
                case 1: output.WriteRawTag(bytes[0]); break;
                case 2: output.WriteRawTag(bytes[0], bytes[1]); break;
                case 3: output.WriteRawTag(bytes[0], bytes[1], bytes[2]); break;
                case 4: output.WriteRawTag(bytes[0], bytes[1], bytes[2], bytes[3]); break;
                default: output.WriteRawTag(bytes[0], bytes[1], bytes[2], bytes[3], bytes[4]); break;
            }
        }
    }
}
