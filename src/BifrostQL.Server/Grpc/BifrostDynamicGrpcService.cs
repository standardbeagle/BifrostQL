using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// The per-call handler behind every dynamically-registered Get/List/Stream RPC. It is resolved
    /// from DI for each gRPC call; the concrete method (which table, which op, which row message) is
    /// bound by the closure <see cref="BifrostGrpcServiceMethodProvider"/> registers, so one handler
    /// class serves the whole generated surface without compiled stubs.
    ///
    /// <para>Every op class resolves identity through the SHARED <see cref="IBifrostAuthContextFactory"/>
    /// off the HTTP/2 request, executes reads through <see cref="IQueryIntentExecutor"/>, and funnels
    /// all faults through <see cref="GrpcStatusMapper"/>. Bearer identity is a later slice: until then
    /// an unauthenticated call yields an empty user context, which the pipeline treats as fail-closed
    /// (scoped to nothing), never full unfiltered data.</para>
    /// </summary>
    internal sealed class BifrostDynamicGrpcService
    {
        private readonly IQueryIntentExecutor _executor;
        private readonly IBifrostAuthContextFactory _authFactory;
        private readonly GrpcWireOptions _options;
        private readonly ILogger<BifrostDynamicGrpcService> _logger;

        public BifrostDynamicGrpcService(
            IQueryIntentExecutor executor,
            IBifrostAuthContextFactory authFactory,
            GrpcWireOptions options,
            ILogger<BifrostDynamicGrpcService> logger)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _authFactory = authFactory ?? throw new ArgumentNullException(nameof(authFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<byte[]> GetAsync(
            IDbTable table, GrpcMessage requestMessage, GrpcMessage rowMessage,
            byte[] request, ServerCallContext context)
            => GrpcStatusMapper.GuardAsync(context, _logger, async () =>
            {
                var userContext = ResolveIdentity(context);
                var values = GrpcMessageCodec.DecodeRequest(requestMessage, request);
                var row = await GrpcReadDispatcher.GetByKeyAsync(
                    _executor, table, values, userContext, _options.Endpoint, context.CancellationToken);

                // A missing row and an out-of-scope row are indistinguishable — no existence oracle.
                if (row is null)
                    throw new RpcException(new Status(StatusCode.NotFound, "No row matches the supplied key."));

                return GrpcMessageCodec.EncodeGetResponse(rowMessage, row);
            });

        public Task<byte[]> ListAsync(
            IDbTable table, GrpcMessage rowMessage, byte[] request, ServerCallContext context)
            => GrpcStatusMapper.GuardAsync(context, _logger, async () =>
            {
                var userContext = ResolveIdentity(context);
                var limit = Math.Min(_options.ListPageSize, _options.MaxStreamRows);
                var rows = await GrpcReadDispatcher.ListAsync(
                    _executor, table, limit, userContext, _options.Endpoint, context.CancellationToken);

                return GrpcMessageCodec.EncodeListResponse(rowMessage, rows);
            });

        public Task StreamAsync(
            IDbTable table, GrpcMessage rowMessage,
            IServerStreamWriter<byte[]> responseStream, ServerCallContext context)
            => GrpcStatusMapper.GuardAsync(context, _logger, async () =>
            {
                var userContext = ResolveIdentity(context);

                // The stream is HARD-BOUNDED by config: the read intent caps rows at MaxStreamRows, so a
                // full-table stream can never emit unbounded rows (invariant 6). WriteAsync provides
                // HTTP/2 flow-control backpressure — the server does not push faster than the client reads.
                var rows = await GrpcReadDispatcher.ListAsync(
                    _executor, table, _options.MaxStreamRows, userContext, _options.Endpoint,
                    context.CancellationToken);

                foreach (var row in rows)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    await responseStream.WriteAsync(GrpcMessageCodec.EncodeRow(rowMessage, row));
                }
            });

        /// <summary>
        /// Projects the caller's identity through the shared fail-closed factory. An RpcException
        /// (e.g. our own NotFound) is deliberately allowed to propagate to the funnel unchanged; the
        /// factory throwing on an unmapped issuer is a fail-closed condition the funnel sanitizes.
        /// </summary>
        private IDictionary<string, object?> ResolveIdentity(ServerCallContext context)
        {
            var http = context.GetHttpContext();
            return _authFactory.CreateUserContext(http);
        }
    }
}
