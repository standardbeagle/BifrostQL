using BifrostQL.Core.Model;
using Grpc.AspNetCore.Server.Model;
using Grpc.Core;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Registers the gRPC read surface DYNAMICALLY at endpoint-build time — one Get/List/Stream RPC
    /// per table in the full contract — with no compiled <c>.proto</c> stubs. Each method uses a raw
    /// <c>byte[]</c> marshaller (the wire bytes pass through untouched) and a closure that binds the
    /// table + row message; the contract-driven <see cref="GrpcMessageCodec"/> does the protobuf
    /// encode/decode inside <see cref="BifrostDynamicGrpcService"/>.
    ///
    /// <para>The method SET is the full model (routing is not authorization); every call is scoped by
    /// the caller's identity through the pipeline, and reflection advertises only the permitted subset.
    /// This is Grpc.AspNetCore's supported dynamic-service seam
    /// (<see cref="IServiceMethodProvider{TService}"/>).</para>
    /// </summary>
    internal sealed class BifrostGrpcServiceMethodProvider : IServiceMethodProvider<BifrostDynamicGrpcService>
    {
        private static readonly Marshaller<byte[]> BytesMarshaller =
            Marshallers.Create(value => value, bytes => bytes);

        private readonly GrpcContractProvider _contracts;

        public BifrostGrpcServiceMethodProvider(GrpcContractProvider contracts)
            => _contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));

        public void OnServiceMethodDiscovery(ServiceMethodProviderContext<BifrostDynamicGrpcService> context)
        {
            // Building the contract here is also the fail-fast descriptor gate at endpoint build.
            var contract = _contracts.FullContract;
            var model = _contracts.Model;
            var serviceName = _contracts.ServiceFullName;
            var messages = contract.Messages.ToDictionary(m => m.Name, StringComparer.Ordinal);
            var tables = model.Tables.ToDictionary(t => t.GraphQlName, StringComparer.Ordinal);

            foreach (var rpc in contract.Service.Methods)
            {
                var table = tables[TableNameOf(rpc)];
                if (rpc.ServerStreaming)
                {
                    // Streaming output type is the row message itself; the input type carries the
                    // shared filter/sort/page request shape the compiler consumes.
                    var requestMessage = messages[rpc.InputType];
                    var rowMessage = messages[rpc.OutputType];
                    var method = new Method<byte[], byte[]>(
                        MethodType.ServerStreaming, serviceName, rpc.Name, BytesMarshaller, BytesMarshaller);
                    context.AddServerStreamingMethod(
                        method, Array.Empty<object>(),
                        (service, request, responseStream, callContext) =>
                            service.StreamAsync(table, requestMessage, rowMessage, request, responseStream, callContext));
                }
                else if (rpc.Name.StartsWith("Get", StringComparison.Ordinal))
                {
                    var requestMessage = messages[rpc.InputType];
                    var rowMessage = messages[RowMessageOf(rpc.OutputType)];
                    var method = new Method<byte[], byte[]>(
                        MethodType.Unary, serviceName, rpc.Name, BytesMarshaller, BytesMarshaller);
                    context.AddUnaryMethod(
                        method, Array.Empty<object>(),
                        (service, request, callContext) =>
                            service.GetAsync(table, requestMessage, rowMessage, request, callContext));
                }
                else // List
                {
                    var requestMessage = messages[rpc.InputType];
                    var rowMessage = messages[$"{table.GraphQlName}Row"];
                    var method = new Method<byte[], byte[]>(
                        MethodType.Unary, serviceName, rpc.Name, BytesMarshaller, BytesMarshaller);
                    context.AddUnaryMethod(
                        method, Array.Empty<object>(),
                        (service, request, callContext) =>
                            service.ListAsync(table, requestMessage, rowMessage, request, callContext));
                }
            }
        }

        /// <summary>The table's GraphQL name is the method name minus its Get/List/Stream verb.</summary>
        private static string TableNameOf(GrpcMethod rpc)
        {
            foreach (var verb in new[] { "Get", "List", "Stream" })
                if (rpc.Name.StartsWith(verb, StringComparison.Ordinal))
                    return rpc.Name[verb.Length..];
            return rpc.Name;
        }

        /// <summary>Maps a <c>Get&lt;Table&gt;Response</c> output type to the <c>&lt;Table&gt;Row</c> message name.</summary>
        private static string RowMessageOf(string getResponseType)
        {
            // "GetUsersResponse" -> "Users" -> "UsersRow"
            var inner = getResponseType["Get".Length..^"Response".Length];
            return $"{inner}Row";
        }
    }
}
