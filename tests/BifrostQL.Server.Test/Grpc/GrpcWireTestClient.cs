using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using BifrostQL.Server.Grpc;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// A minimal gRPC client that encodes Get/List/Stream requests and decodes responses straight from
    /// the slice-1/2 <see cref="GrpcContract"/> — the client-side inverse of the server's
    /// <c>GrpcMessageCodec</c>. It proves a caller with only the generated contract (as reflection
    /// yields) can drive the surface with no compiled stubs, and that the wire round-trips.
    /// </summary>
    internal sealed class GrpcWireTestClient
    {
        private const uint WireVarint = 0;
        private const uint WireFixed64 = 1;
        private const uint WireLengthDelimited = 2;
        private const uint WireFixed32 = 5;
        private const string ServiceName = "bifrostql.BifrostQuery";

        private static readonly Marshaller<byte[]> Bytes = Marshallers.Create(v => v, b => b);

        private readonly CallInvoker _invoker;
        private readonly GrpcContract _contract;

        public GrpcWireTestClient(CallInvoker invoker, GrpcContract contract)
        {
            _invoker = invoker;
            _contract = contract;
        }

        private GrpcMessage Message(string name) => _contract.Messages.Single(m => m.Name == name);

        public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(
            string table, IReadOnlyDictionary<string, object?> key, Metadata? headers = null,
            DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            var request = EncodeMessage(Message($"Get{table}Request"), key);
            var method = new Method<byte[], byte[]>(MethodType.Unary, ServiceName, $"Get{table}", Bytes, Bytes);
            var options = new CallOptions(headers, deadline, cancellationToken);
            try
            {
                var response = await _invoker.AsyncUnaryCall(method, null, options, request);
                // GetResponse.row (field 1) is the nested row message.
                var nested = ReadNestedMessages(response, fieldNumber: 1);
                return nested.Count == 0 ? null : DecodeRow(Message($"{table}Row"), nested[0]);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                // A missing row and an out-of-scope row are indistinguishable — both null, no oracle.
                return null;
            }
        }

        /// <summary>A List page: the decoded rows plus the opaque continuation token (null on the last page).</summary>
        public sealed record ListPage(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, string? NextPageToken);

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAsync(
            string table, Metadata? headers = null,
            string? filter = null, string? orderBy = null, int? pageSize = null, string? pageToken = null)
            => (await ListPageAsync(table, headers, filter, orderBy, pageSize, pageToken)).Rows;

        public async Task<ListPage> ListPageAsync(
            string table, Metadata? headers = null,
            string? filter = null, string? orderBy = null, int? pageSize = null, string? pageToken = null)
        {
            var request = EncodeReadRequest($"List{table}Request", filter, orderBy, pageSize, pageToken);
            var method = new Method<byte[], byte[]>(MethodType.Unary, ServiceName, $"List{table}", Bytes, Bytes);
            var options = new CallOptions(headers);
            var response = await _invoker.AsyncUnaryCall(method, null, options, request);
            var rowMessage = Message($"{table}Row");
            var rows = ReadNestedMessages(response, fieldNumber: 1).Select(b => DecodeRow(rowMessage, b)).ToList();
            return new ListPage(rows, ReadStringField(response, fieldNumber: 2));
        }

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StreamAsync(
            string table, Metadata? headers = null, CancellationToken cancellationToken = default,
            string? filter = null, string? orderBy = null, int? pageSize = null, string? pageToken = null)
        {
            var request = EncodeReadRequest($"Stream{table}Request", filter, orderBy, pageSize, pageToken);
            var method = new Method<byte[], byte[]>(MethodType.ServerStreaming, ServiceName, $"Stream{table}", Bytes, Bytes);
            var options = new CallOptions(headers, cancellationToken: cancellationToken);
            using var call = _invoker.AsyncServerStreamingCall(method, null, options, request);
            var rowMessage = Message($"{table}Row");
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            await foreach (var msg in call.ResponseStream.ReadAllAsync(cancellationToken))
                rows.Add(DecodeRow(rowMessage, msg));
            return rows;
        }

        /// <summary>The decoded mutation result: the affected row count and (insert only) the generated identity.</summary>
        public sealed record MutationResult(long AffectedRows, string? ReturnedKey);

        public Task<MutationResult> InsertAsync(
            string table, IReadOnlyDictionary<string, object?> columns, Metadata? headers = null)
            => MutateAsync($"Insert{table}", $"Insert{table}Request", $"Insert{table}Response", columns, headers);

        public Task<MutationResult> UpdateAsync(
            string table, IReadOnlyDictionary<string, object?> keyAndColumns, Metadata? headers = null)
            => MutateAsync($"Update{table}", $"Update{table}Request", $"Update{table}Response", keyAndColumns, headers);

        public Task<MutationResult> DeleteAsync(
            string table, IReadOnlyDictionary<string, object?> key, Metadata? headers = null)
            => MutateAsync($"Delete{table}", $"Delete{table}Request", $"Delete{table}Response", key, headers);

        private async Task<MutationResult> MutateAsync(
            string methodName, string requestType, string responseType,
            IReadOnlyDictionary<string, object?> values, Metadata? headers)
        {
            var request = EncodeMessage(Message(requestType), values);
            var method = new Method<byte[], byte[]>(MethodType.Unary, ServiceName, methodName, Bytes, Bytes);
            var response = await _invoker.AsyncUnaryCall(method, null, new CallOptions(headers), request);
            var decoded = DecodeRow(Message(responseType), response);
            var affected = decoded.TryGetValue("affected_rows", out var a) && a is not null
                ? Convert.ToInt64(a, CultureInfo.InvariantCulture) : 0L;
            var key = decoded.TryGetValue("returned_key", out var k) ? k?.ToString() : null;
            return new MutationResult(affected, key);
        }

        /// <summary>
        /// Invokes a unary method by NAME with a raw payload, bypassing the contract's message lookup —
        /// used to probe a method that may not exist (a disabled or non-allow-listed write RPC), which
        /// must surface as UNIMPLEMENTED, indistinguishable from a genuinely unknown method.
        /// </summary>
        public async Task RawUnaryAsync(string methodName, byte[] payload, Metadata? headers = null)
        {
            var method = new Method<byte[], byte[]>(MethodType.Unary, ServiceName, methodName, Bytes, Bytes);
            await _invoker.AsyncUnaryCall(method, null, new CallOptions(headers), payload);
        }

        private byte[] EncodeReadRequest(
            string messageName, string? filter, string? orderBy, int? pageSize, string? pageToken)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (filter is not null) values["filter"] = filter;
            if (orderBy is not null) values["order_by"] = orderBy;
            if (pageSize is not null) values["page_size"] = pageSize;
            if (pageToken is not null) values["page_token"] = pageToken;
            return EncodeMessage(Message(messageName), values);
        }

        // ---- encode ----

        private static byte[] EncodeMessage(GrpcMessage message, IReadOnlyDictionary<string, object?> values)
        {
            using var buffer = new MemoryStream();
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                foreach (var field in message.Fields)
                {
                    if (!values.TryGetValue(field.Name, out var value) || value is null)
                        continue;
                    if (field.Scalar == GrpcScalarKind.Timestamp)
                    {
                        WriteTag(output, field.Number, WireLengthDelimited);
                        output.WriteMessage(Timestamp.FromDateTime(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc)));
                        continue;
                    }
                    WriteScalar(output, field.Number, field.Scalar!.Value, value);
                }
            }
            return buffer.ToArray();
        }

        private static void WriteScalar(CodedOutputStream output, int number, GrpcScalarKind kind, object value)
        {
            switch (kind)
            {
                case GrpcScalarKind.Int32: WriteTag(output, number, WireVarint); output.WriteInt32(Convert.ToInt32(value, CultureInfo.InvariantCulture)); break;
                case GrpcScalarKind.Int64: WriteTag(output, number, WireVarint); output.WriteInt64(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
                case GrpcScalarKind.Bool: WriteTag(output, number, WireVarint); output.WriteBool(Convert.ToBoolean(value, CultureInfo.InvariantCulture)); break;
                case GrpcScalarKind.Double: WriteTag(output, number, WireFixed64); output.WriteDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture)); break;
                case GrpcScalarKind.Float: WriteTag(output, number, WireFixed32); output.WriteFloat(Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
                case GrpcScalarKind.Bytes: WriteTag(output, number, WireLengthDelimited); output.WriteBytes(ByteString.CopyFrom((byte[])value)); break;
                default: WriteTag(output, number, WireLengthDelimited); output.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty); break;
            }
        }

        // ---- decode ----

        public static IReadOnlyDictionary<string, object?> DecodeRow(GrpcMessage rowMessage, byte[] bytes)
        {
            var byNumber = rowMessage.Fields.ToDictionary(f => f.Number);
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            var input = new CodedInputStream(bytes);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                var number = (int)(tag >> 3);
                if (!byNumber.TryGetValue(number, out var field)) { input.SkipLastField(); continue; }
                values[field.Name] = ReadScalar(input, field.Scalar!.Value);
            }
            return values;
        }

        private static string? ReadStringField(byte[] bytes, int fieldNumber)
        {
            var input = new CodedInputStream(bytes);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if ((int)(tag >> 3) == fieldNumber && (tag & 7) == WireLengthDelimited)
                    return input.ReadString();
                input.SkipLastField();
            }
            return null;
        }

        private static List<byte[]> ReadNestedMessages(byte[] bytes, int fieldNumber)
        {
            var result = new List<byte[]>();
            var input = new CodedInputStream(bytes);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if ((int)(tag >> 3) == fieldNumber && (tag & 7) == WireLengthDelimited)
                    result.Add(input.ReadBytes().ToByteArray());
                else
                    input.SkipLastField();
            }
            return result;
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

        private static void WriteTag(CodedOutputStream output, int fieldNumber, uint wireType)
        {
            var tag = (uint)(fieldNumber << 3) | wireType;
            Span<byte> raw = stackalloc byte[5];
            var length = 0;
            while (tag >= 0x80) { raw[length++] = (byte)(tag | 0x80); tag >>= 7; }
            raw[length++] = (byte)tag;
            switch (length)
            {
                case 1: output.WriteRawTag(raw[0]); break;
                case 2: output.WriteRawTag(raw[0], raw[1]); break;
                case 3: output.WriteRawTag(raw[0], raw[1], raw[2]); break;
                case 4: output.WriteRawTag(raw[0], raw[1], raw[2], raw[3]); break;
                default: output.WriteRawTag(raw[0], raw[1], raw[2], raw[3], raw[4]); break;
            }
        }
    }
}
