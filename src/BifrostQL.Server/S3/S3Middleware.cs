using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.S3
{
    /// <summary>
    /// The opt-in S3-compatible HTTP front door. Enforces request size limits before any
    /// buffering, authenticates the request via <see cref="S3SigV4Verifier"/> (SigV4 header or
    /// presigned GET), and maps every failure to a deterministic S3 XML error envelope.
    ///
    /// <para>This slice is routing + auth + errors only: GetObject/PutObject data operations
    /// are deferred to later slices, so an authenticated data request answers
    /// <c>501 NotImplemented</c>. Auth is still fully enforced first, so the negative-path
    /// contract (bad signature, expired/skewed date, excessive expiry, missing/altered signed
    /// material, unknown/disabled key) is exercised without any data path existing yet.</para>
    ///
    /// <para>Only <see cref="S3ProtocolException"/> — a deliberately user-facing type with a
    /// curated message — is mapped onto the wire verbatim; every other exception maps to a
    /// generic sanitized InternalError with detail logged server-side only
    /// (.claude/rules/protocol-adapter-security.md invariants 1 and 3).</para>
    /// </summary>
    public sealed class S3Middleware
    {
        private readonly RequestDelegate _next;
        private readonly S3Options _options;
        private readonly S3SigV4Verifier _verifier;
        private readonly S3Listing _listing;
        private readonly ILogger<S3Middleware> _logger;

        public S3Middleware(
            RequestDelegate next,
            S3Options options,
            S3SigV4Verifier verifier,
            S3Listing listing,
            ILogger<S3Middleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
            _listing = listing ?? throw new ArgumentNullException(nameof(listing));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var requestId = S3XmlError.NewRequestId();
            try
            {
                EnforceLimits(context.Request);

                // Cancellation is honored before any auth work so a client abort short-circuits
                // the HMAC/PBKDF2 cost.
                context.RequestAborted.ThrowIfCancellationRequested();

                var userContext = await _verifier.VerifyAsync(context.Request, context.RequestAborted);

                await DispatchAsync(context, userContext, requestId);
            }
            catch (S3ProtocolException ex)
            {
                await WriteErrorAsync(context, ex.HttpStatus, ex.Code, ex.Message, requestId);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client went away; nothing to write.
            }
            catch (Exception ex)
            {
                // Never forward an internal exception message onto the wire (invariant 3):
                // log full detail server-side, return a generic sanitized envelope.
                _logger.LogError(ex, "Unhandled error in S3 endpoint (request {RequestId}).", requestId);
                var internalError = S3ProtocolException.InternalError();
                await WriteErrorAsync(context, internalError.HttpStatus, internalError.Code, internalError.Message, requestId);
            }
        }

        /// <summary>
        /// Routes an authenticated request to the operation its path/method/query name.
        /// Only the list operations this slice implements (ListBuckets, ListObjectsV2)
        /// are handled; every other authenticated request — GetObject/PutObject and the
        /// legacy ListObjects v1 — is a clean, authenticated 501 until its slice lands.
        /// </summary>
        private async Task DispatchAsync(HttpContext context, IDictionary<string, object?> userContext, string requestId)
        {
            var request = context.Request;
            // Inside the mounted branch the path is the remainder after the S3 prefix:
            // "/" (or empty) is the service root, "/bucket" is a bucket, and anything
            // with a further segment addresses an object.
            var resource = (request.Path.Value ?? string.Empty).Trim('/');

            if (!HttpMethods.IsGet(request.Method))
                throw S3ProtocolException.NotImplemented();

            if (resource.Length == 0)
            {
                var buckets = await _listing.ListBucketsAsync(userContext, context.RequestAborted);
                var ownerId = OwnerId(userContext);
                await WriteXmlAsync(context, S3ListXml.ListAllMyBuckets(buckets, ownerId, ownerId), requestId);
                return;
            }

            // A bucket-level GET carries no object key (no further '/'). An object-level
            // GET (GetObject) is a later slice.
            if (resource.Contains('/'))
                throw S3ProtocolException.NotImplemented();

            // ListObjectsV2 is selected by list-type=2; the legacy v1 listing is a non-goal.
            if (request.Query["list-type"].ToString() != "2")
                throw S3ProtocolException.NotImplemented();

            var page = await _listing.ListObjectsV2Async(
                bucket: resource,
                prefix: NullIfEmpty(request.Query["prefix"].ToString()),
                delimiter: NullIfEmpty(request.Query["delimiter"].ToString()),
                maxKeys: ParseMaxKeys(request.Query["max-keys"].ToString()),
                continuationToken: NullIfEmpty(request.Query["continuation-token"].ToString()),
                startAfter: NullIfEmpty(request.Query["start-after"].ToString()),
                userContext: userContext,
                cancellationToken: context.RequestAborted);

            await WriteXmlAsync(context, S3ListXml.ListObjectsV2(page), requestId);
        }

        private static int? ParseMaxKeys(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;
            // TryParse, never int.Parse: a malformed or out-of-range value maps to a
            // clean protocol error, never an escaping exception (invariant 5).
            if (!int.TryParse(raw, out var value))
                throw S3ProtocolException.InvalidArgument("max-keys is not a valid integer.");
            return value;
        }

        private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static string OwnerId(IDictionary<string, object?> userContext)
            => userContext.TryGetValue("user_id", out var id) && id is not null
                ? id.ToString() ?? "bifrost"
                : "bifrost";

        private static async Task WriteXmlAsync(HttpContext context, string xml, string requestId)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/xml";
            context.Response.Headers["x-amz-request-id"] = requestId;
            await context.Response.WriteAsync(xml, context.RequestAborted);
        }

        /// <summary>
        /// Enforces URL, header, and body size caps BEFORE any payload is buffered. The body
        /// cap is checked from Content-Length so an oversized upload is rejected without
        /// reading it (invariant: bound the cost of adversarial input up front).
        /// </summary>
        private void EnforceLimits(HttpRequest request)
        {
            var urlLength = (request.PathBase.Value?.Length ?? 0)
                + (request.Path.Value?.Length ?? 0)
                + (request.QueryString.Value?.Length ?? 0);
            if (urlLength > _options.MaxUrlLength)
                throw S3ProtocolException.InvalidArgument("Request URL is too long.");

            long headerBytes = 0;
            foreach (var header in request.Headers)
            {
                headerBytes += header.Key.Length;
                foreach (var value in header.Value)
                    headerBytes += value?.Length ?? 0;
                if (headerBytes > _options.MaxHeaderBytes)
                    throw S3ProtocolException.InvalidArgument("Request headers are too large.");
            }

            if (request.ContentLength is { } length && length > _options.MaxBodyBytes)
                throw S3ProtocolException.EntityTooLarge();
        }

        private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message, string requestId)
        {
            if (context.Response.HasStarted)
                return;

            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/xml";
            context.Response.Headers["x-amz-request-id"] = requestId;
            await context.Response.WriteAsync(S3XmlError.Write(code, message, requestId), context.RequestAborted);
        }
    }
}
