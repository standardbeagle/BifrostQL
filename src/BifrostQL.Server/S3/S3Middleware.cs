using System.Globalization;
using BifrostQL.Core.Storage;
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
        private readonly FileObjectSeam _seam;
        private readonly ILogger<S3Middleware> _logger;

        public S3Middleware(
            RequestDelegate next,
            S3Options options,
            S3SigV4Verifier verifier,
            S3Listing listing,
            FileObjectSeam seam,
            ILogger<S3Middleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
            _listing = listing ?? throw new ArgumentNullException(nameof(listing));
            _seam = seam ?? throw new ArgumentNullException(nameof(seam));
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
        /// The service root and bucket level serve the list operations (GET only);
        /// an object-level path serves GetObject (GET) and HeadObject (HEAD). Every
        /// other authenticated request — writes, and the legacy ListObjects v1 — is a
        /// clean, authenticated 501.
        /// </summary>
        private async Task DispatchAsync(HttpContext context, IDictionary<string, object?> userContext, string requestId)
        {
            var request = context.Request;
            // Inside the mounted branch the path is the remainder after the S3 prefix:
            // "/" (or empty) is the service root, "/bucket" is a bucket, and anything
            // with a further segment addresses an object.
            var resource = (request.Path.Value ?? string.Empty).Trim('/');

            var isGet = HttpMethods.IsGet(request.Method);
            var isHead = HttpMethods.IsHead(request.Method);
            if (!isGet && !isHead)
                throw S3ProtocolException.NotImplemented();

            if (resource.Length == 0)
            {
                if (!isGet)
                    throw S3ProtocolException.NotImplemented();
                var buckets = await _listing.ListBucketsAsync(userContext, context.RequestAborted);
                var ownerId = OwnerId(userContext);
                await WriteXmlAsync(context, S3ListXml.ListAllMyBuckets(buckets, ownerId, ownerId), requestId);
                return;
            }

            var slash = resource.IndexOf('/');

            // Bucket-level: no object key (no further '/'). ListObjectsV2 only.
            if (slash < 0)
            {
                if (!isGet)
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
                return;
            }

            // Object-level: GetObject (GET) / HeadObject (HEAD).
            await GetOrHeadObjectAsync(
                context, userContext, requestId,
                bucket: resource[..slash], key: resource[(slash + 1)..], headOnly: isHead);
        }

        /// <summary>
        /// Serves GetObject/HeadObject through the authorized <see cref="FileObjectSeam"/>:
        /// the row is resolved under the caller's identity FIRST, so a missing,
        /// unauthorized, or object-less row is indistinguishable from a bad address —
        /// all answer <c>NoSuchKey</c>, never confirming existence (non-enumerating).
        /// Conditional headers (RFC 7232) are applied before any range handling; a
        /// single satisfiable range is streamed as 206, an unsatisfiable one is 416,
        /// and anything else is the whole object — always streamed, never buffered.
        /// HEAD returns the identical headers with no body.
        /// </summary>
        private async Task GetOrHeadObjectAsync(
            HttpContext context, IDictionary<string, object?> userContext, string requestId,
            string bucket, string key, bool headOnly)
        {
            FileObjectSeam.ResolvedFileObject? resolved;
            try
            {
                resolved = await _seam.ResolveAsync(bucket, key, userContext, context.RequestAborted);
            }
            catch (InvalidOperationException)
            {
                // Addressing fault: unknown bucket, malformed/wrong-arity key, or a
                // column that is not a file object. Indistinguishable from a missing
                // key by design, so the endpoint is not an existence oracle.
                throw S3ProtocolException.NoSuchKey();
            }

            // A row that does not exist, is not visible to this caller, or holds no
            // object: one answer for all three (non-enumerating error policy).
            if (resolved is null)
                throw S3ProtocolException.NoSuchKey();

            var request = context.Request;

            // Conditional preconditions take effect before range handling.
            var precondition = S3ConditionalRequest.Evaluate(
                ifMatch: NullIfEmpty(request.Headers.IfMatch.ToString()),
                ifNoneMatch: NullIfEmpty(request.Headers.IfNoneMatch.ToString()),
                ifModifiedSince: NullIfEmpty(request.Headers.IfModifiedSince.ToString()),
                ifUnmodifiedSince: NullIfEmpty(request.Headers.IfUnmodifiedSince.ToString()),
                etag: resolved.ETag,
                lastModified: resolved.LastModified);

            if (precondition == S3PreconditionOutcome.PreconditionFailed)
                throw S3ProtocolException.PreconditionFailed();
            if (precondition == S3PreconditionOutcome.NotModified)
            {
                WriteNotModified(context, resolved, requestId);
                return;
            }

            var length = resolved.ContentLength;
            var range = S3RangeParser.Parse(NullIfEmpty(request.Headers.Range.ToString()), length);
            if (range.Kind == S3RangeKind.Unsatisfiable)
            {
                await WriteRangeNotSatisfiableAsync(context, length, requestId);
                return;
            }

            var partial = range.Kind == S3RangeKind.Satisfiable;
            var start = partial ? range.Start : 0;
            var count = partial ? range.Length : length;

            WriteObjectHeaders(context, resolved, requestId, start, count, length, partial);
            context.Response.StatusCode = partial ? 206 : 200;

            // HEAD carries the same headers with no body.
            if (headOnly)
                return;

            await _seam.CopyContentToAsync(resolved, context.Response.Body, start, count, context.RequestAborted);
        }

        /// <summary>
        /// Writes the common object headers for a 200/206 (and the HEAD equivalents):
        /// content type, the served byte count, ETag, last-modified, Accept-Ranges,
        /// persisted user metadata, and — for a partial response — Content-Range.
        /// </summary>
        private static void WriteObjectHeaders(
            HttpContext context, FileObjectSeam.ResolvedFileObject obj, string requestId,
            long start, long count, long totalLength, bool partial)
        {
            var response = context.Response;
            response.ContentType = string.IsNullOrEmpty(obj.ContentType) ? "application/octet-stream" : obj.ContentType;
            response.ContentLength = count;
            response.Headers.AcceptRanges = "bytes";
            response.Headers["x-amz-request-id"] = requestId;
            SetValidatorHeaders(response, obj);
            WriteUserMetadata(response, obj);

            if (partial)
                response.Headers.ContentRange = $"bytes {start}-{start + count - 1}/{totalLength}";
        }

        private static void WriteNotModified(HttpContext context, FileObjectSeam.ResolvedFileObject obj, string requestId)
        {
            var response = context.Response;
            response.StatusCode = 304;
            response.Headers["x-amz-request-id"] = requestId;
            response.Headers.AcceptRanges = "bytes";
            SetValidatorHeaders(response, obj);
            // 304 carries no body and no Content-Length.
        }

        private async Task WriteRangeNotSatisfiableAsync(HttpContext context, long totalLength, string requestId)
        {
            if (context.Response.HasStarted)
                return;

            context.Response.Clear();
            context.Response.StatusCode = 416;
            context.Response.ContentType = "application/xml";
            context.Response.Headers["x-amz-request-id"] = requestId;
            // Advertise the valid extent so a client can re-request within bounds.
            context.Response.Headers.ContentRange = $"bytes */{totalLength}";
            await context.Response.WriteAsync(
                S3XmlError.Write("InvalidRange", "The requested range is not satisfiable.", requestId),
                context.RequestAborted);
        }

        private static void SetValidatorHeaders(HttpResponse response, FileObjectSeam.ResolvedFileObject obj)
        {
            if (!string.IsNullOrEmpty(obj.ETag))
                response.Headers.ETag = $"\"{obj.ETag}\"";
            response.Headers.LastModified =
                DateTime.SpecifyKind(obj.LastModified, DateTimeKind.Utc).ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Emits the object's persisted custom metadata as <c>x-amz-meta-*</c> response
        /// headers. An entry whose key or value carries a CR/LF (or other control
        /// character) is skipped rather than written, so a poisoned pointer cannot
        /// inject a header or split the response.
        /// </summary>
        private static void WriteUserMetadata(HttpResponse response, FileObjectSeam.ResolvedFileObject obj)
        {
            foreach (var (name, value) in obj.CustomMetadata)
            {
                if (string.IsNullOrEmpty(name) || ContainsControlChar(name) || ContainsControlChar(value))
                    continue;
                response.Headers["x-amz-meta-" + name] = value;
            }
        }

        private static bool ContainsControlChar(string? value)
        {
            if (value is null)
                return false;
            foreach (var c in value)
                if (char.IsControl(c))
                    return true;
            return false;
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
