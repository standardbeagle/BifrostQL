using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// The opt-in OData v4 HTTP front door. Authenticates the request via
    /// <see cref="ODataAuthenticator"/> (Bearer principal or configured Basic credentials) and
    /// maps every failure to a deterministic OData JSON error envelope.
    ///
    /// <para>This slice is routing + auth + errors only: the service document, metadata (EDMX),
    /// and entity reads are deferred to later slices, so an authenticated request answers
    /// <c>501 NotImplemented</c>. Auth is still fully enforced first, so the negative-path
    /// contract (anonymous, bad Basic credentials, subject-less identity, unmapped issuer) is
    /// exercised without any data path existing yet. The adapter owns only HTTP/OData
    /// parsing + encoding; identity comes from the shared factory and future reads will come
    /// from <c>IQueryIntentExecutor</c>, both injected from DI.</para>
    ///
    /// <para>Only <see cref="ODataProtocolException"/> — a deliberately user-facing type with a
    /// curated message — is mapped onto the wire verbatim; every other exception maps to a
    /// generic sanitized InternalError with detail logged server-side only
    /// (.claude/rules/protocol-adapter-security.md invariants 1 and 3).</para>
    /// </summary>
    public sealed class ODataMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ODataOptions _options;
        private readonly ODataAuthenticator _authenticator;
        private readonly ILogger<ODataMiddleware> _logger;

        public ODataMiddleware(
            RequestDelegate next,
            ODataOptions options,
            ODataAuthenticator authenticator,
            ILogger<ODataMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                // Cancellation is honored before any auth work so a client abort short-circuits
                // the credential comparison cost.
                context.RequestAborted.ThrowIfCancellationRequested();

                // Authenticate first — every request is gated before any operation is dispatched.
                _ = await _authenticator.AuthenticateAsync(context, context.RequestAborted);

                // Slice 1 has no data/metadata path: an authenticated request is a clean 501.
                throw ODataProtocolException.NotImplemented();
            }
            catch (ODataProtocolException ex)
            {
                await WriteErrorAsync(context, ex);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client went away; nothing to write.
            }
            catch (Exception ex)
            {
                // Never forward an internal exception message onto the wire (invariant 3):
                // log full detail server-side, return a generic sanitized envelope.
                _logger.LogError(ex, "Unhandled error in OData endpoint.");
                await WriteErrorAsync(context, ODataProtocolException.InternalError());
            }
        }

        private async Task WriteErrorAsync(HttpContext context, ODataProtocolException ex)
        {
            var response = context.Response;
            if (response.HasStarted)
                return;

            response.StatusCode = ex.HttpStatus;
            response.ContentType = "application/json; charset=utf-8";

            // A 401 must carry a challenge so an interactive client knows how to authenticate.
            if (ex.HttpStatus == 401)
                response.Headers.WWWAuthenticate = $"Basic realm=\"{_options.Realm}\", Bearer";

            // OData v4 error format: {"error":{"code":"...","message":"..."}}.
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("error");
                writer.WriteString("code", ex.Code);
                writer.WriteString("message", ex.Message);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            await response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), context.RequestAborted);
        }
    }
}
