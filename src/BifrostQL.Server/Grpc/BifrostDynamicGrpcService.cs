using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
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
        /// <summary>
        /// Response trailer a <c>Stream</c> sets to <c>true</c> when it stopped at its page size (or
        /// the endpoint's max-query-rows ceiling) while more matching rows existed. A complete
        /// stream carries no such trailer.
        /// </summary>
        internal const string TruncatedTrailer = "bifrost-truncated";

        private readonly IQueryIntentExecutor _executor;
        private readonly IMutationIntentExecutor _mutations;
        private readonly IBifrostAuthContextFactory _authFactory;
        private readonly GrpcWireOptions _options;
        private readonly GrpcPageTokenKey _pageTokenKey;
        private readonly ILogger<BifrostDynamicGrpcService> _logger;

        public BifrostDynamicGrpcService(
            IQueryIntentExecutor executor,
            IMutationIntentExecutor mutations,
            IBifrostAuthContextFactory authFactory,
            GrpcWireOptions options,
            GrpcPageTokenKey pageTokenKey,
            ILogger<BifrostDynamicGrpcService> logger)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
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
                var compiled = await CompileAsync(table, requestMessage, request, userContext);
                var rows = await GrpcReadDispatcher.RunAsync(
                    _executor, compiled.Query, userContext, _options.Endpoint, context.CancellationToken);

                return GrpcMessageCodec.EncodeListResponse(
                    rowMessage, PageOf(rows, compiled.Window), NextPageToken(rows.Count, compiled));
            });

        public Task StreamAsync(
            IDbTable table, GrpcMessage requestMessage, GrpcMessage rowMessage, byte[] request,
            IServerStreamWriter<byte[]> responseStream, ServerCallContext context)
            => GrpcStatusMapper.GuardAsync(context, _logger, async () =>
            {
                var userContext = ResolveIdentity(context);

                // Stream shares the SAME compiler as List, so the same filter/sort/page yields the same
                // ordered rows (criterion 4). The compiled query's Limit (page size, itself clamped to
                // MaxStreamRows and max-query-rows, plus the sentinel row) hard-bounds the stream, so a
                // full-table stream can never emit unbounded rows (invariant 6). WriteAsync provides
                // HTTP/2 flow-control backpressure.
                var compiled = await CompileAsync(table, requestMessage, request, userContext);
                var rows = await GrpcReadDispatcher.RunAsync(
                    _executor, compiled.Query, userContext, _options.Endpoint, context.CancellationToken);

                foreach (var row in PageOf(rows, compiled.Window))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    await responseStream.WriteAsync(GrpcMessageCodec.EncodeRow(rowMessage, row));
                }

                // A stream has no continuation, so a cut is reported explicitly rather than ending
                // like a complete answer (protocol-adapter-security: never truncate silently).
                if (compiled.Window.HasMore(rows.Count))
                    context.ResponseTrailers.Add(TruncatedTrailer, "true");
            });

        /// <summary>
        /// Insert/Update/Delete share this spine: resolve identity FIRST (fail-closed before any intent —
        /// an anonymous/invalid credential never reaches the executor), decode the request, route the
        /// write through <see cref="IMutationIntentExecutor"/>, and encode the uniform result. The
        /// method is only registered at all when writes are globally enabled AND the table is
        /// allow-listed, so a disabled or non-allow-listed surface builds ZERO intent by construction —
        /// this handler cannot even be reached (criterion 1/3). Faults funnel through
        /// <see cref="GrpcStatusMapper"/> exactly like reads, so a concurrency/validation/policy fault
        /// is sanitized (invariant 3) and a scoped-away write reports the same as an absent row.
        /// </summary>
        public Task<byte[]> InsertAsync(
            IDbTable table, GrpcMessage requestMessage, GrpcMessage responseMessage,
            byte[] request, ServerCallContext context)
            => WriteAsync(table, requestMessage, responseMessage, request, context, GrpcMutationDispatcher.InsertAsync);

        public Task<byte[]> UpdateAsync(
            IDbTable table, GrpcMessage requestMessage, GrpcMessage responseMessage,
            byte[] request, ServerCallContext context)
            => WriteAsync(table, requestMessage, responseMessage, request, context, GrpcMutationDispatcher.UpdateAsync);

        public Task<byte[]> DeleteAsync(
            IDbTable table, GrpcMessage requestMessage, GrpcMessage responseMessage,
            byte[] request, ServerCallContext context)
            => WriteAsync(table, requestMessage, responseMessage, request, context, GrpcMutationDispatcher.DeleteAsync);

        private delegate Task<GrpcMutationOutcome> MutationOp(
            IMutationIntentExecutor executor, IDbTable table,
            IReadOnlyDictionary<string, object?> requestValues, IDictionary<string, object?> userContext,
            string? endpoint, CancellationToken cancellationToken);

        private Task<byte[]> WriteAsync(
            IDbTable table, GrpcMessage requestMessage, GrpcMessage responseMessage,
            byte[] request, ServerCallContext context, MutationOp op)
            => GrpcStatusMapper.GuardAsync(context, _logger, async () =>
            {
                // Identity is the FIRST check — an unauthenticated write fails closed before any intent.
                var userContext = ResolveIdentity(context);
                var values = GrpcMessageCodec.DecodeRequest(requestMessage, request);
                var outcome = await op(
                    _mutations, table, values, userContext, _options.Endpoint, context.CancellationToken);
                return GrpcMessageCodec.EncodeMutationResponse(responseMessage, outcome);
            });

        /// <summary>
        /// Compiles a decoded List/Stream request into a programmatic read through the ONE shared
        /// <see cref="GrpcReadRequestCompiler"/>. Field/sort names are validated against the caller's
        /// identity-VISIBLE columns (a hidden column is rejected, not an oracle — invariant 4); every
        /// depth/count/size cap is enforced before the query is built (invariant 6). Both the default
        /// and maximum page size are the endpoint's own bounds, so a caller can never widen the cap and
        /// a Stream stays bounded by MaxStreamRows.
        /// </summary>
        private async Task<GrpcCompiledRead> CompileAsync(
            IDbTable table, GrpcMessage requestMessage, byte[] request, IDictionary<string, object?> userContext)
        {
            var values = GrpcMessageCodec.DecodeRequest(requestMessage, request);
            var visibleColumns = GrpcSchemaVisibility.VisibleReadColumns(table, userContext);
            var model = await _executor.GetModelAsync(_options.Endpoint);
            return GrpcReadRequestCompiler.Compile(
                model, table, visibleColumns, values,
                defaultPageSize: _options.ListPageSize,
                maxPageSize: _options.MaxStreamRows,
                identity: userContext,
                tokenSecret: _pageTokenKey.Secret,
                now: DateTimeOffset.UtcNow,
                ttl: _pageTokenKey.Ttl);
        }

        /// <summary>The rows a List page or a Stream returns: at most the window's page, sentinel dropped.</summary>
        private static IReadOnlyList<IReadOnlyDictionary<string, object?>> PageOf(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, RowProbeWindow window)
            => window.HasMore(rows.Count) ? rows.Take(window.Page).ToList() : rows;

        /// <summary>
        /// Mints the next-page cursor ONLY when the sentinel row came back (more rows exist); a page
        /// that ends exactly at the last row carries no token, so a client never pays a round trip
        /// for an empty final page. The offset advances by the window's page, which the
        /// max-query-rows ceiling may have narrowed below the requested size, so no row is skipped.
        /// An empty page (a ceiling of 1) never advances, so it carries no token either. The token
        /// encodes POSITION only, bound to this table/query/identity — a forged or replayed token
        /// still re-runs through the live pipeline, so it can at most reposition within the caller's
        /// own visible rows (criterion 3).
        /// </summary>
        private string? NextPageToken(int rowCount, GrpcCompiledRead compiled)
            => compiled.Window.HasMore(rowCount) && compiled.Window.Page > 0
                ? GrpcPageCursor.Issue(
                    compiled.Offset + compiled.Window.Page, DateTimeOffset.UtcNow, compiled.Binding, _pageTokenKey.Secret)
                : null;

        /// <summary>
        /// Resolves the caller through <see cref="GrpcIdentityGate"/> — the ONE identity gate every
        /// op class on this port shares, reflection included, so no surface can drift onto a weaker
        /// check (protocol-adapter-security invariants 4, 9, 10).
        /// </summary>
        private IDictionary<string, object?> ResolveIdentity(ServerCallContext context)
            => GrpcIdentityGate.ResolveIdentity(context, _authFactory, _logger);
    }
}
