using System.Runtime.InteropServices;
using Google.Protobuf;

namespace BifrostQL.Server
{
    /// <summary>
    /// Message type for binary transport requests and responses.
    /// Chunk and ChunkAck are used for payloads exceeding the chunk threshold.
    /// Resume and ResumeAck are used for reconnection and chunk retransmission.
    /// ChunkNack requests retransmission of a specific chunk on checksum mismatch.
    /// </summary>
    public enum BifrostMessageType
    {
        Query = 0,
        Mutation = 1,
        Result = 2,
        Error = 3,
        Chunk = 4,
        ChunkAck = 5,
        Resume = 6,
        ResumeAck = 7,
        ChunkNack = 8,
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
    ///   field 7: chunk_sequence (uint32, varint) - chunk index (0-based)
    ///   field 8: chunk_total (uint32, varint) - total number of chunks
    ///   field 9: chunk_offset (uint64, varint) - byte offset of this chunk in the full payload
    ///   field 10: total_bytes (uint64, varint) - total payload size before chunking
    ///   field 11: chunk_checksum (uint32, varint) - CRC32 of this chunk's payload
    ///   field 12: last_sequence (uint32, varint) - last received chunk sequence (Resume messages)
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
        /// The message type (Query, Mutation, Result, Error, Chunk, ChunkAck, Resume, ResumeAck, ChunkNack).
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
        /// For Chunk messages, contains the chunk data fragment.
        /// </summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Error messages. Set on Error and Result messages when errors occur.
        /// </summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>
        /// Zero-based chunk sequence number. Set on Chunk and ChunkAck messages.
        /// </summary>
        public uint ChunkSequence { get; set; }

        /// <summary>
        /// Total number of chunks in the transfer. Set on Chunk messages.
        /// </summary>
        public uint ChunkTotal { get; set; }

        /// <summary>
        /// Byte offset of this chunk within the full payload. Set on Chunk messages.
        /// </summary>
        public ulong ChunkOffset { get; set; }

        /// <summary>
        /// Total size of the full payload before chunking. Set on Chunk messages.
        /// </summary>
        public ulong TotalBytes { get; set; }

        /// <summary>
        /// CRC32 checksum of this chunk's Payload bytes. Set on Chunk messages.
        /// </summary>
        public uint ChunkChecksum { get; set; }

        /// <summary>
        /// The last successfully received chunk sequence number. Set on Resume messages
        /// so the server knows which chunks to retransmit.
        /// A value of uint.MaxValue means no chunks were received (start from 0).
        /// </summary>
        public uint LastSequence { get; set; }

        // Protobuf field tags (field number << 3 | wire type)
        private const int RequestIdTag = (1 << 3) | 0;        // varint
        private const int TypeTag = (2 << 3) | 0;              // varint
        private const int QueryTag = (3 << 3) | 2;             // length-delimited
        private const int VariablesTag = (4 << 3) | 2;         // length-delimited
        private const int PayloadTag = (5 << 3) | 2;           // length-delimited
        private const int ErrorsTag = (6 << 3) | 2;            // length-delimited (repeated)
        private const int ChunkSequenceTag = (7 << 3) | 0;     // varint
        private const int ChunkTotalTag = (8 << 3) | 0;        // varint
        private const int ChunkOffsetTag = (9 << 3) | 0;       // varint
        private const int TotalBytesTag = (10 << 3) | 0;       // varint
        private const int ChunkChecksumTag = (11 << 3) | 0;    // varint
        private const int LastSequenceTag = (12 << 3) | 0;     // varint

        /// <summary>
        /// Serializes this message to protobuf wire format: one array of exactly the frame's size,
        /// with the payload written straight into it (never copied into an intermediate buffer).
        /// </summary>
        public byte[] ToBytes()
        {
            // UnsafeWrap shares Payload without copying; the ByteString lives only for this call.
            var payload = Payload.Length > 0 ? UnsafeByteOperations.UnsafeWrap(Payload) : ByteString.Empty;
            var bytes = new byte[CalculateSize(payload)];
            var output = new CodedOutputStream(bytes);

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
                output.WriteBytes(payload);
            }

            foreach (var error in Errors)
            {
                output.WriteTag(ErrorsTag);
                output.WriteString(error);
            }

            if (ChunkSequence != 0)
            {
                output.WriteTag(ChunkSequenceTag);
                output.WriteUInt32(ChunkSequence);
            }

            if (ChunkTotal != 0)
            {
                output.WriteTag(ChunkTotalTag);
                output.WriteUInt32(ChunkTotal);
            }

            if (ChunkOffset != 0)
            {
                output.WriteTag(ChunkOffsetTag);
                output.WriteUInt64(ChunkOffset);
            }

            if (TotalBytes != 0)
            {
                output.WriteTag(TotalBytesTag);
                output.WriteUInt64(TotalBytes);
            }

            if (ChunkChecksum != 0)
            {
                output.WriteTag(ChunkChecksumTag);
                output.WriteUInt32(ChunkChecksum);
            }

            if (LastSequence != 0)
            {
                output.WriteTag(LastSequenceTag);
                output.WriteUInt32(LastSequence);
            }

            // A size/write mismatch would leave trailing zero bytes the reader parses as field 0.
            output.CheckNoSpaceLeft();
            return bytes;
        }

        /// <summary>The serialized size of this message, field for field as <see cref="ToBytes"/> writes it.</summary>
        private int CalculateSize(ByteString payload)
        {
            var size = 0;
            if (RequestId != 0)
                size += CodedOutputStream.ComputeTagSize(RequestIdTag >> 3) + CodedOutputStream.ComputeUInt32Size(RequestId);
            if (Type != BifrostMessageType.Query)
                size += CodedOutputStream.ComputeTagSize(TypeTag >> 3) + CodedOutputStream.ComputeInt32Size((int)Type);
            if (Query.Length > 0)
                size += CodedOutputStream.ComputeTagSize(QueryTag >> 3) + CodedOutputStream.ComputeStringSize(Query);
            if (VariablesJson.Length > 0)
                size += CodedOutputStream.ComputeTagSize(VariablesTag >> 3) + CodedOutputStream.ComputeStringSize(VariablesJson);
            if (Payload.Length > 0)
                size += CodedOutputStream.ComputeTagSize(PayloadTag >> 3) + CodedOutputStream.ComputeBytesSize(payload);
            foreach (var error in Errors)
                size += CodedOutputStream.ComputeTagSize(ErrorsTag >> 3) + CodedOutputStream.ComputeStringSize(error);
            if (ChunkSequence != 0)
                size += CodedOutputStream.ComputeTagSize(ChunkSequenceTag >> 3) + CodedOutputStream.ComputeUInt32Size(ChunkSequence);
            if (ChunkTotal != 0)
                size += CodedOutputStream.ComputeTagSize(ChunkTotalTag >> 3) + CodedOutputStream.ComputeUInt32Size(ChunkTotal);
            if (ChunkOffset != 0)
                size += CodedOutputStream.ComputeTagSize(ChunkOffsetTag >> 3) + CodedOutputStream.ComputeUInt64Size(ChunkOffset);
            if (TotalBytes != 0)
                size += CodedOutputStream.ComputeTagSize(TotalBytesTag >> 3) + CodedOutputStream.ComputeUInt64Size(TotalBytes);
            if (ChunkChecksum != 0)
                size += CodedOutputStream.ComputeTagSize(ChunkChecksumTag >> 3) + CodedOutputStream.ComputeUInt32Size(ChunkChecksum);
            if (LastSequence != 0)
                size += CodedOutputStream.ComputeTagSize(LastSequenceTag >> 3) + CodedOutputStream.ComputeUInt32Size(LastSequence);
            return size;
        }

        /// <summary>
        /// The bytes of a ByteString the parser just read. ReadBytes copies the field out of the
        /// input into a fresh array of exactly its length; this takes that array instead of
        /// copying it a second time. Nothing else holds the ByteString, so the array is ours.
        /// </summary>
        private static byte[] TakeArray(ByteString bytes)
        {
            if (MemoryMarshal.TryGetArray(bytes.Memory, out var segment)
                && segment.Offset == 0 && segment.Count == segment.Array!.Length)
                return segment.Array;
            return bytes.ToByteArray();
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
                        msg.Payload = TakeArray(input.ReadBytes());
                        break;
                    case ErrorsTag:
                        msg.Errors.Add(input.ReadString());
                        break;
                    case ChunkSequenceTag:
                        msg.ChunkSequence = input.ReadUInt32();
                        break;
                    case ChunkTotalTag:
                        msg.ChunkTotal = input.ReadUInt32();
                        break;
                    case ChunkOffsetTag:
                        msg.ChunkOffset = input.ReadUInt64();
                        break;
                    case TotalBytesTag:
                        msg.TotalBytes = input.ReadUInt64();
                        break;
                    case ChunkChecksumTag:
                        msg.ChunkChecksum = input.ReadUInt32();
                        break;
                    case LastSequenceTag:
                        msg.LastSequence = input.ReadUInt32();
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
