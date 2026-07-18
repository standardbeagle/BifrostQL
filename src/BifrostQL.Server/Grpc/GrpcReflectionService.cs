using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Reflection.V1Alpha;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Server reflection filtered PER CALLER by the same read policy the query path enforces. gRPC
    /// reflection is an introspection surface (protocol-adapter-security invariant 4): a table, RPC,
    /// or column the caller may not read must be ABSENT from what reflection returns, or the reflected
    /// descriptor becomes an information-disclosure oracle. So this does NOT serve a single global
    /// descriptor set — it regenerates the visibility-filtered artifacts for the call's identity
    /// (<see cref="GrpcContractProvider.Generate"/>, the SAME <c>GrpcSchemaVisibility</c>/
    /// <c>PolicyEvaluator</c> filter the artifact export uses) and answers only from that.
    ///
    /// <para>Identity comes from the shared fail-closed <see cref="IBifrostAuthContextFactory"/>; with
    /// no slice-4 bearer identity an unauthenticated caller reflects only what an empty context may
    /// read. A symbol that is denied is reported as NOT_FOUND — identical to a genuinely unknown
    /// symbol, so absence never leaks existence.</para>
    /// </summary>
    internal sealed class GrpcReflectionService : ServerReflection.ServerReflectionBase
    {
        private const string ReflectionServiceName = "grpc.reflection.v1alpha.ServerReflection";

        private readonly GrpcContractProvider _contracts;
        private readonly IBifrostAuthContextFactory _authFactory;
        private readonly ILogger<GrpcReflectionService> _logger;

        public GrpcReflectionService(
            GrpcContractProvider contracts,
            IBifrostAuthContextFactory authFactory,
            ILogger<GrpcReflectionService> logger)
        {
            _contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));
            _authFactory = authFactory ?? throw new ArgumentNullException(nameof(authFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override Task ServerReflectionInfo(
            IAsyncStreamReader<ServerReflectionRequest> requestStream,
            IServerStreamWriter<ServerReflectionResponse> responseStream,
            ServerCallContext context)
            => GrpcStatusMapper.GuardAsync(context, _logger, async () =>
            {
                var userContext = _authFactory.CreateUserContext(context.GetHttpContext());
                var visibleSet = FileDescriptorSet.Parser.ParseFrom(_contracts.Generate(userContext).DescriptorSet);

                await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
                {
                    var response = Answer(request, visibleSet);
                    await responseStream.WriteAsync(response);
                }
            });

        private ServerReflectionResponse Answer(ServerReflectionRequest request, FileDescriptorSet visibleSet)
        {
            var response = new ServerReflectionResponse { OriginalRequest = request };

            switch (request.MessageRequestCase)
            {
                case ServerReflectionRequest.MessageRequestOneofCase.ListServices:
                    response.ListServicesResponse = ListServices(visibleSet);
                    break;

                case ServerReflectionRequest.MessageRequestOneofCase.FileContainingSymbol:
                    response = FilesForSymbol(request, visibleSet, response);
                    break;

                case ServerReflectionRequest.MessageRequestOneofCase.FileByFilename:
                    response = FilesForFilename(request, visibleSet, response);
                    break;

                default:
                    response.ErrorResponse = new ErrorResponse
                    {
                        ErrorCode = (int)StatusCode.Unimplemented,
                        ErrorMessage = "This reflection request is not supported.",
                    };
                    break;
            }

            return response;
        }

        private static ListServiceResponse ListServices(FileDescriptorSet visibleSet)
        {
            var list = new ListServiceResponse();
            foreach (var file in visibleSet.File)
                foreach (var service in file.Service)
                    list.Service.Add(new ServiceResponse
                    {
                        Name = string.IsNullOrEmpty(file.Package) ? service.Name : $"{file.Package}.{service.Name}",
                    });
            list.Service.Add(new ServiceResponse { Name = ReflectionServiceName });
            return list;
        }

        private static ServerReflectionResponse FilesForSymbol(
            ServerReflectionRequest request, FileDescriptorSet visibleSet, ServerReflectionResponse response)
        {
            var symbol = request.FileContainingSymbol;
            var owner = visibleSet.File.FirstOrDefault(f => DeclaredSymbols(f).Contains(symbol));
            if (owner is null)
                return NotFound(response, $"Symbol not found: {symbol}");

            response.FileDescriptorResponse = AllFiles(visibleSet);
            return response;
        }

        private static ServerReflectionResponse FilesForFilename(
            ServerReflectionRequest request, FileDescriptorSet visibleSet, ServerReflectionResponse response)
        {
            var name = request.FileByFilename;
            if (visibleSet.File.All(f => f.Name != name))
                return NotFound(response, $"File not found: {name}");

            response.FileDescriptorResponse = AllFiles(visibleSet);
            return response;
        }

        /// <summary>Returns the whole visible descriptor set (dependencies included) so a client resolves the closure.</summary>
        private static FileDescriptorResponse AllFiles(FileDescriptorSet visibleSet)
        {
            var files = new FileDescriptorResponse();
            foreach (var file in visibleSet.File)
                files.FileDescriptorProto.Add(file.ToByteString());
            return files;
        }

        /// <summary>Every fully-qualified symbol a file declares: its services, methods, messages, and nested messages.</summary>
        private static IEnumerable<string> DeclaredSymbols(FileDescriptorProto file)
        {
            var prefix = string.IsNullOrEmpty(file.Package) ? string.Empty : file.Package + ".";

            foreach (var service in file.Service)
            {
                yield return prefix + service.Name;
                foreach (var method in service.Method)
                    yield return $"{prefix}{service.Name}.{method.Name}";
            }

            foreach (var message in file.MessageType)
                foreach (var symbol in MessageSymbols(prefix, message))
                    yield return symbol;
        }

        private static IEnumerable<string> MessageSymbols(string prefix, DescriptorProto message)
        {
            var name = prefix + message.Name;
            yield return name;
            foreach (var nested in message.NestedType)
                foreach (var symbol in MessageSymbols(name + ".", nested))
                    yield return symbol;
        }

        private static ServerReflectionResponse NotFound(ServerReflectionResponse response, string message)
        {
            response.ErrorResponse = new ErrorResponse
            {
                ErrorCode = (int)StatusCode.NotFound,
                ErrorMessage = message,
            };
            return response;
        }
    }
}
