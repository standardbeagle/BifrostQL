using Google.Protobuf;

namespace BifrostQL.Server
{
    /// <summary>
    /// Message type for binary transport requests and responses.
    /// </summary>
    public enum BifrostMessageType
    {
        Query = 0,
        Mutation = 1,
        Result = 2,
        Error = 3,
    }

    /// <summary>
    /// Protobuf-serializable binary transport envelope for BifrostQL WebSocket communication.
    /// Uses a hand-rolled serialization format compatible with protobuf wire format:
    ///   field 1: request_id (uint32, varint)
    ///   field 2: type (enum/int32, varint)
    ///   field 3: query (string, length-delimited)
    ///   field 4: variables_json (string, length-delimited)
    ///   field 5: payload (bytes, length-delimited)
    ///   field 6: errors (repeated string, length-delimited)
    ///
    /// This uses Google.Protobuf's CodedOutputStream/CodedInputStream directly
    /// for maximum compatibility with any protobuf client library.
    /// </summary>
    public sealed class BifrostMessage
    {
        /// <summary>
        /// Unique request identifier for connection multiplexing.
        /// The response carries the same request_id so clients can match responses to requests.
        /// </summary>
        public uint RequestId { get; set; }

        /// <summary>
        /// The message type (Query, Mutation, Result, Error).
        /// </summary>
        public BifrostMessageType Type { get; set; }

        /// <summary>
        /// GraphQL query or mutation text. Set on request messages.
        /// </summary>
        public string Query { get; set; } = "";

        /// <summary>
        /// JSON-encoded query variables. Set on request messages.
        /// Using JSON encoding for variables preserves the dynamic type structure
        /// without needing per-table protobuf definitions at the envelope level.
        /// </summary>
        public string VariablesJson { get; set; } = "";

        /// <summary>
        /// JSON-encoded response payload. Set on Result messages.
        /// The response data is JSON-encoded because the shape varies per query.
        /// Per-table typed protobuf responses are a future enhancement (proto schema generation).
        /// </summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Error messages. Set on Error and Result messages when errors occur.
        /// </summary>
        public List<string> Errors { get; set; } = new();

        // Protobuf field tags (field number << 3 | wire type)
        private const int RequestIdTag = (1 << 3) | 0;   // varint
        private const int TypeTag = (2 << 3) | 0;         // varint
        private const int QueryTag = (3 << 3) | 2;        // length-delimited
        private const int VariablesTag = (4 << 3) | 2;    // length-delimited
        private const int PayloadTag = (5 << 3) | 2;      // length-delimited
        private const int ErrorsTag = (6 << 3) | 2;       // length-delimited (repeated)

        /// <summary>
        /// Serializes this message to protobuf wire format.
        /// </summary>
        public byte[] ToBytes()
        {
            using var memStream = new MemoryStream();
            var output = new CodedOutputStream(memStream, leaveOpen: true);

            if (RequestId != 0)
            {
                output.WriteTag(RequestIdTag);
                output.WriteUInt32(RequestId);
            }

            if (Type != BifrostMessageType.Query)
            {
                output.WriteTag(TypeTag);
                output.WriteInt32((int)Type);
            }

            if (Query.Length > 0)
            {
                output.WriteTag(QueryTag);
                output.WriteString(Query);
            }

            if (VariablesJson.Length > 0)
            {
                output.WriteTag(VariablesTag);
                output.WriteString(VariablesJson);
            }

            if (Payload.Length > 0)
            {
                output.WriteTag(PayloadTag);
                output.WriteBytes(ByteString.CopyFrom(Payload));
            }

            foreach (var error in Errors)
            {
                output.WriteTag(ErrorsTag);
                output.WriteString(error);
            }

            output.Flush();
            return memStream.ToArray();
        }

        /// <summary>
        /// Deserializes a BifrostMessage from protobuf wire format bytes.
        /// </summary>
        public static BifrostMessage FromBytes(byte[] data)
        {
            return FromBytes(data, 0, data.Length);
        }

        /// <summary>
        /// Deserializes a BifrostMessage from a segment of protobuf wire format bytes.
        /// </summary>
        public static BifrostMessage FromBytes(byte[] data, int offset, int count)
        {
            var msg = new BifrostMessage();
            var input = new CodedInputStream(data, offset, count);

            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                switch (tag)
                {
                    case RequestIdTag:
                        msg.RequestId = input.ReadUInt32();
                        break;
                    case TypeTag:
                        msg.Type = (BifrostMessageType)input.ReadInt32();
                        break;
                    case QueryTag:
                        msg.Query = input.ReadString();
                        break;
                    case VariablesTag:
                        msg.VariablesJson = input.ReadString();
                        break;
                    case PayloadTag:
                        msg.Payload = input.ReadBytes().ToByteArray();
                        break;
                    case ErrorsTag:
                        msg.Errors.Add(input.ReadString());
                        break;
                    default:
                        input.SkipLastField();
                        break;
                }
            }

            return msg;
        }
    }
}
