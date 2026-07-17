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
        private readonly ILogger<S3Middleware> _logger;

        public S3Middleware(
            RequestDelegate next,
            S3Options options,
            S3SigV4Verifier verifier,
            ILogger<S3Middleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
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

                await _verifier.VerifyAsync(context.Request, context.RequestAborted);

                // Data operations (GetObject/PutObject) arrive in later slices. Authentication
                // has already succeeded here, so this is a clean, authenticated 501.
                throw S3ProtocolException.NotImplemented();
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
