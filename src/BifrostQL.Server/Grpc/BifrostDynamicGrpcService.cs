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
        private readonly GrpcPageTokenKey _pageTokenKey;
        private readonly ILogger<BifrostDynamicGrpcService> _logger;

        public BifrostDynamicGrpcService(
            IQueryIntentExecutor executor,
            IBifrostAuthContextFactory authFactory,
            GrpcWireOptions options,
            GrpcPageTokenKey pageTokenKey,
            ILogger<BifrostDynamicGrpcService> logger)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _authFactory = authFactory ?? throw new ArgumentNullException(nameof(authFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _pageTokenKey = pageTokenKey ?? throw new ArgumentNullException(nameof(pageTokenKey));
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
                    throw new RpcException(new Status(StatusCode.NotFound, GrpcStatusMapper.RowNotFoundMessage));

                return GrpcMessageCodec.EncodeGetResponse(rowMessage, row);
            },
            // A row-addressed Get hides an authorization denial as NOT_FOUND so it is indistinguishable
            // from a missing row — see GrpcStatusMapper (criterion 3).
            denialIsNotFound: true);

        public Task<byte[]> ListAsync(
            IDbTable table, GrpcMessage requestMessage, GrpcMessage rowMessage, byte[] request, ServerCallContext context)
            => GrpcStatusMapper.GuardAsync(context, _logger, async () =>
            {
                var userContext = ResolveIdentity(context);
                var compiled = Compile(table, requestMessage, request, userContext);
                var rows = await GrpcReadDispatcher.RunAsync(
                    _executor, compiled.Query, userContext, _options.Endpoint, context.CancellationToken);

                return GrpcMessageCodec.EncodeListResponse(rowMessage, rows, NextPageToken(rows.Count, compiled));
            });

        public Task StreamAsync(
            IDbTable table, GrpcMessage requestMessage, GrpcMessage rowMessage, byte[] request,
            IServerStreamWriter<byte[]> responseStream, ServerCallContext context)
            => GrpcStatusMapper.GuardAsync(context, _logger, async () =>
            {
                var userContext = ResolveIdentity(context);

                // Stream shares the SAME compiler as List, so the same filter/sort/page yields the same
                // ordered rows (criterion 4). The compiled query's Limit (page size, itself clamped to
                // MaxStreamRows) hard-bounds the stream, so a full-table stream can never emit unbounded
                // rows (invariant 6). WriteAsync provides HTTP/2 flow-control backpressure.
                var compiled = Compile(table, requestMessage, request, userContext);
                var rows = await GrpcReadDispatcher.RunAsync(
                    _executor, compiled.Query, userContext, _options.Endpoint, context.CancellationToken);

                foreach (var row in rows)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    await responseStream.WriteAsync(GrpcMessageCodec.EncodeRow(rowMessage, row));
                }
            });

        /// <summary>
        /// Compiles a decoded List/Stream request into a programmatic read through the ONE shared
        /// <see cref="GrpcReadRequestCompiler"/>. Field/sort names are validated against the caller's
        /// identity-VISIBLE columns (a hidden column is rejected, not an oracle — invariant 4); every
        /// depth/count/size cap is enforced before the query is built (invariant 6). Both the default
        /// and maximum page size are the endpoint's own bounds, so a caller can never widen the cap and
        /// a Stream stays bounded by MaxStreamRows.
        /// </summary>
        private GrpcCompiledRead Compile(
            IDbTable table, GrpcMessage requestMessage, byte[] request, IDictionary<string, object?> userContext)
        {
            var values = GrpcMessageCodec.DecodeRequest(requestMessage, request);
            var visibleColumns = GrpcSchemaVisibility.VisibleReadColumns(table, userContext);
            return GrpcReadRequestCompiler.Compile(
                table, visibleColumns, values,
                defaultPageSize: _options.ListPageSize,
                maxPageSize: _options.MaxStreamRows,
                identity: userContext,
                tokenSecret: _pageTokenKey.Secret,
                now: DateTimeOffset.UtcNow,
                ttl: _pageTokenKey.Ttl);
        }

        /// <summary>
        /// Mints the next-page cursor ONLY when the page came back full (another page may follow); a
        /// short page is the last page and carries no token. The token encodes POSITION only
        /// (offset + page size) bound to this table/query/identity — a forged or replayed token still
        /// re-runs through the live pipeline, so it can at most reposition within the caller's own
        /// visible rows (criterion 3).
        /// </summary>
        private string? NextPageToken(int rowCount, GrpcCompiledRead compiled)
            => rowCount < compiled.PageSize
                ? null
                : GrpcPageCursor.Issue(
                    compiled.Offset + compiled.PageSize, DateTimeOffset.UtcNow, compiled.Binding, _pageTokenKey.Secret);

        /// <summary>
        /// The largest <c>authorization</c> credential the adapter will look at. A real bearer/JWT is
        /// well under this; the cap exists only to reject an abusive/oversized metadata value cleanly
        /// (a fixed-work fail-closed) before any projection, never an unbounded parse/alloc
        /// (criterion 5). It sits below Kestrel's own header-size limit so the adapter — not the host
        /// — returns the clean UNAUTHENTICATED.
        /// </summary>
        private const int MaxAuthorizationChars = 8 * 1024;

        /// <summary>
        /// Extracts the caller's bearer credential and projects it into a Bifrost user context through
        /// the SHARED <see cref="IBifrostAuthContextFactory"/> — the same seam OData/MCP/S3 use. The
        /// adapter does NOT decide claims/identity mapping itself; it only (a) enforces a size cap on
        /// the raw <c>authorization</c> credential and (b) FAILS CLOSED before any intent is built when
        /// the projection is empty (missing/anonymous), throws (unmapped issuer, subject-less), or the
        /// credential is abusive. There is NO branch that reaches the executor with a permissive or
        /// anonymous identity (criterion 1 / invariant 2). Every failure surfaces the SAME sanitized
        /// UNAUTHENTICATED — the real cause is logged server-side only (invariants 2, 3).
        /// </summary>
        private IDictionary<string, object?> ResolveIdentity(ServerCallContext context)
        {
            var http = context.GetHttpContext();

            var authorizationLength = 0;
            foreach (var value in http.Request.Headers.Authorization)
                authorizationLength += value?.Length ?? 0;
            if (authorizationLength > MaxAuthorizationChars)
            {
                _logger.LogWarning(
                    "gRPC authorization credential exceeded {Cap} chars ({Actual}); failing closed.",
                    MaxAuthorizationChars, authorizationLength);
                throw GrpcRequestException.Unauthenticated();
            }

            IDictionary<string, object?> projected;
            try
            {
                projected = _authFactory.CreateUserContext(http);
            }
            catch (Exception ex)
            {
                // Unmapped OIDC issuer, subject-less principal, or any projection fault — fail closed.
                // The detail (issuer name, claim shape) is logged server-side only, never on the wire.
                _logger.LogWarning(ex, "gRPC identity projection failed; failing closed.");
                throw GrpcRequestException.Unauthenticated();
            }

            // An empty context is the shared factory's fail-closed signal for a missing/anonymous
            // credential. Reject BEFORE building any intent so a credential-less call can never reach
            // the executor with a permissive identity.
            if (projected.Count == 0)
                throw GrpcRequestException.Unauthenticated();

            return projected;
        }
    }
}
