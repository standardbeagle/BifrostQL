using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// The opt-in Prometheus <c>/metrics</c> HTTP front door. The credential gate
    /// (<see cref="PrometheusScrapeGate"/>) is the FIRST check — before any model lookup, collection,
    /// or cache read — and every denial is UNIFORM: the same 401 whether the credential is absent,
    /// wrong, or the surface is disarmed, with a sanitized body that carries no secret and no
    /// Bifrost-internal detail (protocol-adapter-security invariants 2/3). Request limits and
    /// cancellation bound an abusive or aborted scrape (criterion 5); OpenMetrics negotiation is a
    /// non-goal.
    ///
    /// <para>An authorized scrape delegates to <see cref="PrometheusScrapeService"/>, whose reads all
    /// cross <see cref="Core.Resolvers.IQueryIntentExecutor"/> under each metric's resolved scope. Any
    /// unexpected error is logged server-side and answered with a generic 500 — never a verbatim
    /// internal message on the wire (invariant 3).</para>
    /// </summary>
    public sealed class PrometheusMetricsMiddleware
    {
        private const string DenialBody = "# unauthorized\n";

        private readonly RequestDelegate _next;
        private readonly PrometheusScrapeGate _gate;
        private readonly PrometheusScrapeService _service;
        private readonly PrometheusExpositionOptions _options;
        private readonly ILogger<PrometheusMetricsMiddleware> _logger;

        public PrometheusMetricsMiddleware(
            RequestDelegate next,
            PrometheusScrapeGate gate,
            PrometheusScrapeService service,
            PrometheusExpositionOptions options,
            ILogger<PrometheusMetricsMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                context.RequestAborted.ThrowIfCancellationRequested();

                // GATE FIRST — before method/body parsing or any model/collection/cache work. A
                // denied scrape builds zero intent and gets one uniform 401.
                if (!_gate.IsAuthorized(ExtractCredential(context.Request)))
                {
                    await WriteDenialAsync(context);
                    return;
                }

                // A scrape is a body-less GET/HEAD; bound method and body (criterion 5).
                if (!IsScrapeMethod(context.Request.Method))
                {
                    await WriteStatusAsync(context, StatusCodes.Status405MethodNotAllowed);
                    return;
                }

                if (BodyExceedsLimit(context.Request))
                {
                    await WriteStatusAsync(context, StatusCodes.Status413PayloadTooLarge);
                    return;
                }

                var body = await _service.ScrapeAsync(context.RequestAborted);
                await WriteExpositionAsync(context, body);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The scraper went away — the in-flight collection is torn down by its token; nothing
                // to write.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error serving Prometheus /metrics scrape.");
                await WriteStatusAsync(context, StatusCodes.Status500InternalServerError, "# internal error\n");
            }
        }

        // The presented credential from the Authorization: Bearer header. Any other scheme or an
        // absent header yields null → the gate denies uniformly.
        private static string? ExtractCredential(HttpRequest request)
        {
            var header = request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return header[prefix.Length..].Trim();
            return null;
        }

        private static bool IsScrapeMethod(string method) =>
            HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

        private bool BodyExceedsLimit(HttpRequest request) =>
            request.ContentLength is { } length && length > _options.MaxRequestBodyBytes;

        private static async Task WriteDenialAsync(HttpContext context)
        {
            var response = context.Response;
            if (response.HasStarted)
                return;
            response.StatusCode = StatusCodes.Status401Unauthorized;
            response.Headers.WWWAuthenticate = "Bearer";
            response.ContentType = "text/plain; charset=utf-8";
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes(DenialBody), context.RequestAborted);
        }

        private static async Task WriteStatusAsync(HttpContext context, int status, string? body = null)
        {
            var response = context.Response;
            if (response.HasStarted)
                return;
            response.StatusCode = status;
            response.ContentType = "text/plain; charset=utf-8";
            if (body is not null)
                await response.Body.WriteAsync(Encoding.UTF8.GetBytes(body), context.RequestAborted);
        }

        private static async Task WriteExpositionAsync(HttpContext context, string body)
        {
            var response = context.Response;
            if (response.HasStarted)
                return;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = PrometheusExpositionWriter.ContentType;
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes(body), context.RequestAborted);
        }
    }
}
