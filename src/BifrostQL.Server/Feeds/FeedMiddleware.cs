using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Feeds
{
    /// <summary>
    /// A feed protocol violation that maps to a fixed, sanitized HTTP status: an unsupported method, an
    /// unknown/non-feed table, or a malformed request shape. A deliberately user-facing type carrying a
    /// GENERIC message only — it never embeds the table name, the caller, or any internal detail
    /// (.claude/rules/protocol-adapter-security.md invariant 3). "Unknown table" and "read-denied table"
    /// both collapse to the SAME 404 so the endpoint is never an existence oracle (invariants 9/10).
    /// </summary>
    public sealed class FeedProtocolException : Exception
    {
        private FeedProtocolException(int status, string message) : base(message) => HttpStatus = status;

        /// <summary>The HTTP status this violation maps to.</summary>
        public int HttpStatus { get; }

        /// <summary>405 — only GET/HEAD are served at a feed route.</summary>
        public static FeedProtocolException MethodNotAllowed() => new(405, "Method not allowed.");

        /// <summary>404 — the feed does not exist, is not a feed, or is not visible to the caller (one surface).</summary>
        public static FeedProtocolException NotFound() => new(404, "Feed not found.");

        /// <summary>400 — the request shape (since/limit) was malformed.</summary>
        public static FeedProtocolException BadRequest() => new(400, "Invalid feed request.");
    }

    /// <summary>
    /// The opt-in syndication-feed HTTP front door. Mounted on its own <c>/feeds</c> branch by
    /// <see cref="BifrostFeedExtensions.UseBifrostFeeds"/>; it never alters the GraphQL/binary/other
    /// protocol routes. The adapter owns ONLY HTTP method/route/negotiation + XML encoding — identity is
    /// projected through the shared <see cref="FeedAuthenticator"/> (fail-closed) and every read crosses
    /// <see cref="FeedReadPlanner"/> → <see cref="IQueryIntentExecutor"/>, so tenant/soft-delete/policy
    /// scope apply unskippably and the adapter builds no predicate of its own beyond the declared
    /// <c>since</c> bound.
    ///
    /// <para><b>Single error funnel</b> (.claude/rules/protocol-adapter-security.md invariant 10): every
    /// op class (GET/HEAD, suffix/Accept negotiation, 200/304) is wrapped in ONE top-level try/catch —
    /// there are no per-branch catches to drift. <see cref="FeedAuthException"/> → a bare uniform 401 with
    /// no distinguishing body; <see cref="OperationCanceledException"/> on client abort never becomes a
    /// wire response; a Bifrost-internal error (<see cref="BifrostExecutionError"/>/<see cref="FeedException"/>)
    /// is logged server-side and mapped to the SAME sanitized 404 an unknown table produces, so
    /// "read-denied" is indistinguishable from "unknown" (no existence oracle).</para>
    ///
    /// <para><b>Auth first</b> (invariants 4/11 spirit): the caller is authenticated for the requested
    /// table BEFORE any model lookup, planning, cache evaluation, or rendering, so an unauthenticated
    /// request gets the identical 401 for every table name — existence is never leaked before the gate.</para>
    /// </summary>
    public sealed class FeedMiddleware
    {
        private const string TokenQueryKey = "token";
        private const string AtomMediaType = "application/atom+xml";
        private const string RssContentType = "application/rss+xml; charset=utf-8";
        private const string AtomContentType = "application/atom+xml; charset=utf-8";

        private readonly RequestDelegate _next;
        private readonly FeedEndpointOptions _options;
        private readonly FeedOptions _feed;
        private readonly FeedAuthenticator _authenticator;
        private readonly FeedReadPlanner _planner;
        private readonly IQueryIntentExecutor _reads;
        private readonly ILogger<FeedMiddleware> _logger;

        public FeedMiddleware(
            RequestDelegate next,
            FeedEndpointOptions options,
            FeedOptions feed,
            FeedAuthenticator authenticator,
            FeedReadPlanner planner,
            IQueryIntentExecutor reads,
            ILogger<FeedMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _feed = feed ?? throw new ArgumentNullException(nameof(feed));
            _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            _planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Snapshot the principal so a residual projection mutation from a FAILED authentication is
            // undone before any downstream (logging, diagnostics) observes it — a failed auth must leave
            // HttpContext.User untouched and mint no context (slice-3 review advisory). Kept in-scope:
            // the feed front door restores it rather than the shared authenticator seam.
            var originalUser = context.User;

            // A query-string token leaks into intermediary/access logs; mark EVERY response to such a
            // request no-store so a shared cache never retains it, and set it up-front so it is present on
            // errors and 304s alike (slice-3 advisory). Bearer requests get a plain private marker.
            var carriesQueryToken = context.Request.Query.ContainsKey(TokenQueryKey);
            context.Response.Headers.CacheControl = carriesQueryToken ? "private, no-store" : "private";

            try
            {
                context.RequestAborted.ThrowIfCancellationRequested();

                if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                    throw FeedProtocolException.MethodNotAllowed();

                var (tableName, explicitFormat) = ParseRoute(context.Request.Path.Value);

                // Authenticate FIRST — before model lookup, planning, cache, or rendering. The requested
                // table name gates a scoped feed token's allow-list; a Bearer principal is projected as-is.
                var userContext = await _authenticator.AuthenticateAsync(context, tableName, context.RequestAborted);

                var format = Negotiate(explicitFormat, context.Request.Headers.Accept.ToString());
                // Bounded parse: a malformed/overflowing since/limit collapses to a clean 400 (invariant 5).
                var request = FeedRequest.Parse(
                    context.Request.Query["since"].ToString(), context.Request.Query["limit"].ToString());

                // Resolve the requested table against the cached model AFTER auth — an unauthenticated
                // request never reaches a model lookup, so table existence cannot leak before the gate.
                var model = await _reads.GetModelAsync(_options.Endpoint);
                var table = ResolveFeedTable(model, tableName)
                    ?? throw FeedProtocolException.NotFound();

                var document = await _planner.BuildAsync(
                    table, request, userContext, _feed, _options.Endpoint, context.RequestAborted);

                var conditional = FeedConditionalRequest.Evaluate(
                    document, format,
                    FeedConditionalRequest.IdentityPartition(userContext),
                    context.Request.Headers.IfNoneMatch.ToString(),
                    context.Request.Headers.IfModifiedSince.ToString());

                await WriteFeedAsync(context, document, format, conditional);
            }
            catch (FeedAuthException)
            {
                // A cancellation the authenticator seam may have collapsed into an auth failure must not
                // surface as a 401 — honor the client abort instead (slice-3 advisory).
                if (context.RequestAborted.IsCancellationRequested)
                    return;
                context.User = originalUser; // undo any residual projection mutation on failure
                await WriteStatusAsync(context, FeedAuthException.Status);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client went away / shutdown — never a wire response.
            }
            catch (FeedRequestException)
            {
                await WriteStatusAsync(context, 400);
            }
            catch (FeedProtocolException ex)
            {
                await WriteStatusAsync(context, ex.HttpStatus);
            }
            catch (Exception ex) when (ex is FeedException or BifrostExecutionError)
            {
                // A feed-config/data-shape fault or a Bifrost-internal read error (incl. a table-level
                // read-deny) maps to the SAME sanitized 404 an unknown table gives — no oracle. Full
                // detail is logged server-side only (invariant 3).
                _logger.LogWarning(ex, "Feed request could not be served for a resolved caller; returning sanitized 404.");
                await WriteStatusAsync(context, 404);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in feed endpoint.");
                await WriteStatusAsync(context, 500);
            }
        }

        /// <summary>
        /// Splits the route into the requested table name and any explicit format suffix. Only <c>.rss</c>
        /// and <c>.atom</c> are recognized as suffixes; a table name may itself contain dots (a schema-
        /// qualified name such as <c>public.posts</c>), so any OTHER trailing extension is left as part of
        /// the table name and simply resolves to a not-found feed — the documented "unsupported format"
        /// behavior. A multi-segment or empty path is a not-found.
        /// </summary>
        private static (string Table, FeedFormat? Format) ParseRoute(string? path)
        {
            var segment = (path ?? string.Empty).Trim('/');
            if (segment.Length == 0 || segment.Contains('/'))
                throw FeedProtocolException.NotFound();

            if (segment.EndsWith(".rss", StringComparison.OrdinalIgnoreCase))
                return (Require(segment[..^4]), FeedFormat.Rss);
            if (segment.EndsWith(".atom", StringComparison.OrdinalIgnoreCase))
                return (Require(segment[..^5]), FeedFormat.Atom);

            return (segment, null);

            static string Require(string table)
                => table.Length > 0 ? table : throw FeedProtocolException.NotFound();
        }

        /// <summary>
        /// Deterministic format selection: an explicit <c>.rss</c>/<c>.atom</c> suffix wins; otherwise an
        /// <c>Accept</c> header naming <c>application/atom+xml</c> selects Atom; otherwise RSS 2.0 is the
        /// default. An unrecognized <c>Accept</c> falls through to the RSS default rather than a 406.
        /// </summary>
        private static FeedFormat Negotiate(FeedFormat? explicitFormat, string acceptHeader)
        {
            if (explicitFormat is { } chosen)
                return chosen;
            return acceptHeader.Contains(AtomMediaType, StringComparison.OrdinalIgnoreCase)
                ? FeedFormat.Atom
                : FeedFormat.Rss;
        }

        /// <summary>
        /// Resolves the requested table to a FEED (a table declaring a feed timestamp) from the cached
        /// model, or <c>null</c> when the name is unknown OR the table is not a feed — both collapse to
        /// the same not-found so a non-feed table is never distinguishable from a missing one. The name is
        /// matched against the GraphQL name, the schema-qualified GraphQL name, and the raw db name so the
        /// route can use whichever convention a scoped token's allow-list uses.
        /// </summary>
        private static IDbTable? ResolveFeedTable(IDbModel model, string name)
        {
            foreach (var table in model.Tables)
            {
                var qualified = $"{table.TableSchema}.{table.GraphQlName}";
                if (string.Equals(table.GraphQlName, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(qualified, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(table.DbName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return FeedConfig.FromTable(table).IsEnabled ? table : null;
                }
            }
            return null;
        }

        private async Task WriteFeedAsync(
            HttpContext context, FeedDocument document, FeedFormat format, FeedConditionalRequest conditional)
        {
            var response = context.Response;
            if (response.HasStarted)
                return;

            // Validators go on both the 200 and the 304 so a subsequent conditional request can match.
            response.Headers.ETag = conditional.ETag;
            response.Headers.LastModified = conditional.LastModified.ToString("r", System.Globalization.CultureInfo.InvariantCulture);

            if (conditional.NotModified)
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            var body = format == FeedFormat.Atom ? AtomFeedWriter.Write(document) : RssFeedWriter.Write(document);
            var bytes = Encoding.UTF8.GetBytes(body);

            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = format == FeedFormat.Atom ? AtomContentType : RssContentType;
            response.ContentLength = bytes.Length;

            // HEAD carries the identical headers (status, content-type, length, validators) with no body.
            if (HttpMethods.IsHead(context.Request.Method))
                return;

            await response.Body.WriteAsync(bytes.AsMemory(), context.RequestAborted);
        }

        /// <summary>
        /// Writes a sanitized status-only response. A 401 carries a <c>WWW-Authenticate</c> challenge and
        /// NO body — every auth-failure class is byte-identical on the wire. Other statuses carry no
        /// distinguishing detail either.
        /// </summary>
        private static Task WriteStatusAsync(HttpContext context, int status)
        {
            var response = context.Response;
            if (response.HasStarted)
                return Task.CompletedTask;

            response.StatusCode = status;
            response.ContentLength = 0;
            if (status == 401)
                response.Headers.WWWAuthenticate = "Bearer";
            if (status == 405)
                response.Headers.Allow = "GET, HEAD";

            return Task.CompletedTask;
        }
    }
}
