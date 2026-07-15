using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Cdc;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for CDC slice 5 — the HTTP webhook <see cref="IEventSink"/>. The sink POSTs the
/// exact serialized CloudEvents body, HMAC-SHA256 signs it keyed on the model-level
/// <see cref="MetadataKeys.Cdc.WebhookSecret"/> value, supports comma-separated secret rotation
/// (one signature per active secret), and sends the CloudEvents <c>id</c> as an idempotency
/// header. No live network: the HttpMessageHandler is stubbed. Signatures are verified against an
/// INDEPENDENTLY computed HMAC, never against the sink's own helper.
/// </summary>
public class WebhookEventSinkTests
{
    private static readonly Uri Endpoint = new("https://receiver.example/hook");

    private static JsonObject Envelope(string id = "4821") => new()
    {
        ["specversion"] = "1.0",
        ["id"] = id,
        ["source"] = "dbo.orders",
        ["type"] = "bifrostql.dbo.orders.update",
        ["subject"] = "1007",
        ["data"] = new JsonObject { ["id"] = 1007, ["status"] = "shipped" },
    };

    // Independent reference HMAC — deliberately NOT calling the sink's ComputeSignatures, so the
    // test proves the wire signature against a from-scratch computation.
    private static string ReferenceSignature(byte[] body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    [Fact]
    public async Task Signs_the_exact_posted_body_with_hmac_the_receiver_can_verify()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var sink = new WebhookEventSink(new HttpClient(handler), Endpoint, () => new[] { "top-secret" });

        var result = await sink.DeliverAsync(Envelope(), "4821", CancellationToken.None);

        result.Should().Be(EventDeliveryResult.Delivered);
        handler.Method.Should().Be(HttpMethod.Post);
        handler.RequestUri.Should().Be(Endpoint);

        // The signature must verify against the EXACT bytes that were posted.
        var expected = ReferenceSignature(handler.Body!, "top-secret");
        handler.Signatures.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public async Task Sends_the_cloudevents_id_as_the_idempotency_key_header()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var sink = new WebhookEventSink(new HttpClient(handler), Endpoint, () => new[] { "s" });

        await sink.DeliverAsync(Envelope("4821"), "4821", CancellationToken.None);

        handler.IdempotencyKey.Should().Be("4821");
    }

    [Fact]
    public async Task Rotation_signs_with_every_active_secret_so_old_and_new_both_verify()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        // Mid-rotation: the model value is "old,new" — a receiver still trusting only the OLD
        // secret must still find a verifiable signature (no downtime, no restart).
        var sink = new WebhookEventSink(
            new HttpClient(handler), Endpoint, () => WebhookEventSink.ParseSecrets(" old , new "));

        await sink.DeliverAsync(Envelope(), "4821", CancellationToken.None);

        handler.Signatures.Should().HaveCount(2);
        handler.Signatures.Should().Contain(ReferenceSignature(handler.Body!, "old"));
        handler.Signatures.Should().Contain(ReferenceSignature(handler.Body!, "new"));
    }

    [Fact]
    public async Task Non_success_status_is_a_transient_failure_not_a_throw()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        var sink = new WebhookEventSink(new HttpClient(handler), Endpoint, () => new[] { "s" });

        var result = await sink.DeliverAsync(Envelope(), "4821", CancellationToken.None);

        result.Should().Be(EventDeliveryResult.TransientFailure);
    }

    [Fact]
    public async Task With_no_active_secret_it_refuses_to_send_and_reports_transient_failure()
    {
        // Fail-closed: a misconfigured (empty) secret must never produce an UNSIGNED delivery.
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var sink = new WebhookEventSink(new HttpClient(handler), Endpoint, () => Array.Empty<string>());

        var result = await sink.DeliverAsync(Envelope(), "4821", CancellationToken.None);

        result.Should().Be(EventDeliveryResult.TransientFailure);
        handler.WasCalled.Should().BeFalse("an unsigned delivery must never reach the wire");
    }

    // Throwing-substitute for the error-mapping seam (SendAsync): a transport-level exception
    // must be mapped to TransientFailure, never escape, and leak nothing to the caller.
    [Fact]
    public async Task Transport_exception_is_mapped_to_transient_failure_and_swallowed()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused to 10.0.0.5"));
        var sink = new WebhookEventSink(new HttpClient(handler), Endpoint, () => new[] { "s" });

        var result = await sink.DeliverAsync(Envelope(), "4821", CancellationToken.None);

        result.Should().Be(EventDeliveryResult.TransientFailure);
    }

    [Fact]
    public async Task Cancellation_propagates_rather_than_masquerading_as_transient_failure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new ThrowingHandler(new OperationCanceledException());
        var sink = new WebhookEventSink(new HttpClient(handler), Endpoint, () => new[] { "s" });

        var act = () => sink.DeliverAsync(Envelope(), "4821", cts.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void ParseSecrets_drops_blanks_from_trailing_and_duplicate_commas()
    {
        WebhookEventSink.ParseSecrets("a,,b, ,c,").Should().Equal("a", "b", "c");
        WebhookEventSink.ParseSecrets(null).Should().BeEmpty();
        WebhookEventSink.ParseSecrets("   ").Should().BeEmpty();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public CapturingHandler(HttpStatusCode status) => _status = status;

        public bool WasCalled { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public byte[]? Body { get; private set; }
        public IReadOnlyList<string> Signatures { get; private set; } = Array.Empty<string>();
        public string? IdempotencyKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Signatures = request.Headers.TryGetValues(WebhookEventSink.SignatureHeader, out var sigs)
                ? sigs.ToList()
                : Array.Empty<string>();
            IdempotencyKey = request.Headers.TryGetValues(WebhookEventSink.IdempotencyHeader, out var keys)
                ? keys.Single()
                : null;
            return new HttpResponseMessage(_status);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _toThrow;
        public ThrowingHandler(Exception toThrow) => _toThrow = toThrow;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw _toThrow;
    }
}
