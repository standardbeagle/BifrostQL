using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Modules.Cdc
{
    /// <summary>
    /// An <see cref="IEventSink"/> that HTTP-POSTs each CloudEvents envelope drained from the
    /// transactional outbox to a single configured endpoint, HMAC-SHA256 signed. Owns only the
    /// wire: it never retries, never re-schedules, never touches the outbox — a non-2xx response
    /// or a network fault becomes <see cref="EventDeliveryResult.TransientFailure"/> and the
    /// <see cref="OutboxDispatcher"/> decides backoff / dead-letter.
    ///
    /// <para><b>Signing.</b> The body is serialized ONCE and the SAME bytes are both signed and
    /// sent, so a receiver's HMAC over the received body matches byte-for-byte. Each signature is
    /// <c>sha256=&lt;hex&gt;</c>, HMAC-SHA256 keyed on a secret from the model-level
    /// <see cref="MetadataKeys.Cdc.WebhookSecret"/> value.</para>
    ///
    /// <para><b>Rotation.</b> The secret value is a comma-separated list; the sink signs with
    /// EVERY active secret and emits one <see cref="SignatureHeader"/> value per secret. A receiver
    /// mid-rotation verifies against whichever secret it currently trusts and always sees at least
    /// one verifiable signature — no downtime, no restart, no dropped deliveries while old and new
    /// secrets overlap.</para>
    ///
    /// <para><b>Idempotency.</b> The CloudEvents <c>id</c> (<paramref name="idempotencyKey"/>) is
    /// sent as the <see cref="IdempotencyHeader"/> so at-least-once redelivery is de-dupable at the
    /// receiver.</para>
    ///
    /// <para><b>Secret hygiene.</b> The secret and the computed signature are NEVER logged, and a
    /// non-2xx response body is never read onto any surface (it could echo the signed payload). A
    /// send fault is logged by exception TYPE only — never a Bifrost-internal message or raw body
    /// (protocol-adapter-security rule 3).</para>
    /// </summary>
    public sealed class WebhookEventSink : IEventSink
    {
        /// <summary>Multi-valued header carrying one <c>sha256=&lt;hex&gt;</c> signature per active secret.</summary>
        public const string SignatureHeader = "X-Bifrost-Signature";

        /// <summary>Carries the CloudEvents <c>id</c> so the receiver can de-duplicate redeliveries.</summary>
        public const string IdempotencyHeader = "Idempotency-Key";

        /// <summary>Configuration key naming the single webhook endpoint the sink POSTs to.</summary>
        public const string EndpointConfigKey = "Cdc:WebhookUrl";

        private const string SignatureScheme = "sha256=";

        private readonly HttpClient _http;
        private readonly Uri _endpoint;
        private readonly Func<IReadOnlyList<string>> _activeSecrets;
        private readonly ILogger? _logger;

        /// <param name="http">The HTTP client used to POST. The sink does not own its lifetime.</param>
        /// <param name="endpoint">The absolute URL every delivery is POSTed to.</param>
        /// <param name="activeSecrets">Resolves the CURRENT active signing secrets each delivery, so a
        /// secret rotated in the model's <see cref="MetadataKeys.Cdc.WebhookSecret"/> takes effect on the
        /// next delivery without a restart.</param>
        public WebhookEventSink(
            HttpClient http,
            Uri endpoint,
            Func<IReadOnlyList<string>> activeSecrets,
            ILogger? logger = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _activeSecrets = activeSecrets ?? throw new ArgumentNullException(nameof(activeSecrets));
            _logger = logger;
        }

        /// <inheritdoc />
        public async ValueTask<EventDeliveryResult> DeliverAsync(
            JsonObject envelope, string idempotencyKey, CancellationToken cancellationToken)
        {
            if (envelope is null) throw new ArgumentNullException(nameof(envelope));
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));

            // Serialize ONCE and sign the exact bytes we send — a re-serialize could differ
            // (member order, escaping) and break the receiver's byte-for-byte HMAC check.
            var body = Encoding.UTF8.GetBytes(envelope.ToJsonString());

            var signatures = ComputeSignatures(body, _activeSecrets());
            if (signatures.Count == 0)
            {
                // No active secret: refuse to send an UNSIGNED delivery (fail-closed). An
                // unsigned webhook the receiver cannot authenticate is worse than a retry; the
                // dispatcher re-schedules and eventually dead-letters if it stays misconfigured.
                _logger?.LogError(
                    "CDC webhook sink has no active '{Key}' secret; refusing to send an unsigned delivery.",
                    MetadataKeys.Cdc.WebhookSecret);
                return EventDeliveryResult.TransientFailure;
            }

            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
            foreach (var signature in signatures)
                request.Headers.TryAddWithoutValidation(SignatureHeader, SignatureScheme + signature);
            request.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);

            return await SendAsync(request, cancellationToken);
        }

        // The ONE error-mapping seam: turn any transport outcome into the sink's two-valued
        // contract. Success ⇒ Delivered; everything else ⇒ TransientFailure with a sanitized,
        // secret-free, body-free server-side log line.
        private async ValueTask<EventDeliveryResult> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return EventDeliveryResult.Delivered;

                // Status code ONLY — never the response body (it may echo our signed payload)
                // and never the secret/signature.
                _logger?.LogWarning(
                    "CDC webhook delivery returned {Status}; will retry.", (int)response.StatusCode);
                return EventDeliveryResult.TransientFailure;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Cooperative shutdown — let the dispatcher's loop observe cancellation.
            }
            catch (Exception ex)
            {
                // Network / timeout / DNS: transient. Log the exception TYPE only — never a
                // Bifrost-internal message or raw body onto any client-visible surface.
                _logger?.LogWarning("CDC webhook delivery failed ({Error}); will retry.", ex.GetType().Name);
                return EventDeliveryResult.TransientFailure;
            }
        }

        /// <summary>
        /// HMAC-SHA256 the exact body once per active secret, returning a lowercase hex digest per
        /// secret. Multiple digests are how a receiver mid secret-rotation always sees a signature
        /// it can verify. Blank/empty secrets are skipped so a trailing comma never yields a
        /// zero-key HMAC.
        /// </summary>
        internal static IReadOnlyList<string> ComputeSignatures(byte[] body, IReadOnlyList<string> secrets)
        {
            var digests = new List<string>(secrets.Count);
            foreach (var secret in secrets)
            {
                if (string.IsNullOrEmpty(secret)) continue;
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                digests.Add(Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant());
            }
            return digests;
        }

        /// <summary>
        /// Parses a comma-separated <see cref="MetadataKeys.Cdc.WebhookSecret"/> value into the
        /// active secret list (rotation: sign with ALL of them). Trims whitespace and drops empty
        /// entries so a trailing/duplicate comma never produces a blank secret.
        /// </summary>
        public static IReadOnlyList<string> ParseSecrets(string? raw) =>
            string.IsNullOrWhiteSpace(raw)
                ? Array.Empty<string>()
                : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
