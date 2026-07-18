using Google.Protobuf;
using Grpc.Core;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Emits the gRPC RICH error model (the <c>grpc-status-details-bin</c> trailer carrying a
    /// serialized <c>google.rpc.Status</c> whose <c>details</c> hold a <c>google.rpc.BadRequest</c>)
    /// for a validation fault — WITHOUT a compiled proto stub or the <c>Grpc.StatusProto</c> /
    /// <c>Google.Api.CommonProtos</c> package. The three well-known messages are hand-encoded with
    /// the already-referenced <see cref="CodedOutputStream"/>, exactly as the rest of this stubless
    /// adapter builds protobuf on the wire.
    ///
    /// <para>Only adapter-owned <see cref="GrpcRequestException.FieldViolations"/> (which reference
    /// request field names) are ever emitted here; a <c>BifrostExecutionError</c> or internal
    /// exception never reaches this path — it maps to a bare generic INTERNAL status with no detail
    /// (invariant 3).</para>
    /// </summary>
    internal static class GrpcRichError
    {
        // Binary metadata key the gRPC spec reserves for the google.rpc.Status detail model.
        private const string StatusDetailsTrailer = "grpc-status-details-bin";
        private const string BadRequestTypeUrl = "type.googleapis.com/google.rpc.BadRequest";

        private const uint WireVarint = 0;
        private const uint WireLengthDelimited = 2;

        /// <summary>
        /// Builds the status-details trailer for <paramref name="ex"/>, or null when it carries no
        /// field violations (in which case the bare <see cref="Status"/> already says everything the
        /// caller may see).
        /// </summary>
        public static Metadata? TrailersFor(GrpcRequestException ex, StatusCode statusCode, string message)
        {
            if (ex.FieldViolations.Count == 0)
                return null;

            var badRequest = EncodeBadRequest(ex.FieldViolations);
            var any = EncodeAny(BadRequestTypeUrl, badRequest);
            // google.rpc.Status.code shares google.rpc.Code's numbering with Grpc.Core.StatusCode.
            var status = EncodeStatus((int)statusCode, message, any);

            var trailers = new Metadata();
            trailers.Add(StatusDetailsTrailer, status);
            return trailers;
        }

        // google.rpc.Status { int32 code = 1; string message = 2; repeated Any details = 3; }
        private static byte[] EncodeStatus(int code, string message, byte[] anyDetail)
            => Message(output =>
            {
                WriteTag(output, 1, WireVarint);
                output.WriteInt32(code);
                WriteString(output, 2, message);
                WriteBytesField(output, 3, anyDetail);
            });

        // google.protobuf.Any { string type_url = 1; bytes value = 2; }
        private static byte[] EncodeAny(string typeUrl, byte[] value)
            => Message(output =>
            {
                WriteString(output, 1, typeUrl);
                WriteBytesField(output, 2, value);
            });

        // google.rpc.BadRequest { repeated FieldViolation field_violations = 1; }
        // FieldViolation { string field = 1; string description = 2; }
        private static byte[] EncodeBadRequest(IReadOnlyList<GrpcFieldViolation> violations)
            => Message(output =>
            {
                foreach (var violation in violations)
                {
                    var fv = Message(inner =>
                    {
                        WriteString(inner, 1, violation.Field);
                        WriteString(inner, 2, violation.Description);
                    });
                    WriteBytesField(output, 1, fv);
                }
            });

        private static byte[] Message(Action<CodedOutputStream> write)
        {
            using var buffer = new MemoryStream();
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
                write(output);
            return buffer.ToArray();
        }

        private static void WriteString(CodedOutputStream output, int field, string value)
        {
            WriteTag(output, field, WireLengthDelimited);
            output.WriteString(value);
        }

        private static void WriteBytesField(CodedOutputStream output, int field, byte[] value)
        {
            WriteTag(output, field, WireLengthDelimited);
            output.WriteBytes(UnsafeByteOperations.UnsafeWrap(value));
        }

        // Field numbers here are all < 16, so the tag is a single varint byte.
        private static void WriteTag(CodedOutputStream output, int field, uint wireType)
            => output.WriteTag((uint)(field << 3) | wireType);
    }
}
