using System.Text.Json;
using BifrostQL.UI.NativeBridge;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Unit tests for BridgeDispatcher — the Photino-free core of the native bridge.
/// Covers envelope validation, handler routing, result/error framing, the
/// secret-scrubbing of handler exceptions, and push events. Outbound envelopes
/// are captured through the injected send delegate, so no webview is required.
/// </summary>
public sealed class BridgeDispatcherTests
{
    private readonly List<string> _sent = new();
    private readonly BridgeDispatcher _d;

    public BridgeDispatcherTests() => _d = new BridgeDispatcher(_sent.Add);

    private JsonElement LastEnvelope()
    {
        _sent.Should().NotBeEmpty("an envelope should have been sent");
        return JsonDocument.Parse(_sent[^1]).RootElement;
    }

    private static Func<JsonElement, CancellationToken, Task<object?>> Handler(Func<JsonElement, object?> fn)
        => (payload, _) => Task.FromResult(fn(payload));

    [Fact]
    public async Task ValidRequest_RoutesToHandler_AndFramesResult()
    {
        _d.Register("echo", Handler(_ => new { ok = true }));

        await _d.DispatchAsync("""{"id":"r1","kind":"echo","payload":{"x":1}}""");

        var env = LastEnvelope();
        env.GetProperty("id").GetString().Should().Be("r1");
        env.GetProperty("kind").GetString().Should().Be("result");
        env.GetProperty("payload").GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Handler_ReceivesPayloadElement()
    {
        _d.Register("add1", Handler(p => p.GetProperty("x").GetInt32() + 1));

        await _d.DispatchAsync("""{"id":"r","kind":"add1","payload":{"x":41}}""");

        LastEnvelope().GetProperty("payload").GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task UnknownKind_ReturnsError()
    {
        await _d.DispatchAsync("""{"id":"r","kind":"nope"}""");

        var env = LastEnvelope();
        env.GetProperty("kind").GetString().Should().Be("error");
        env.GetProperty("payload").GetProperty("message").GetString()
            .Should().Contain("No handler registered");
    }

    [Fact]
    public async Task MissingKind_ReturnsError()
    {
        await _d.DispatchAsync("""{"id":"r"}""");

        LastEnvelope().GetProperty("payload").GetProperty("message").GetString()
            .Should().Be("Missing kind");
    }

    [Fact]
    public async Task MissingId_IsDroppedSilently()
    {
        await _d.DispatchAsync("""{"kind":"echo"}""");

        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedJson_IsDroppedSilently()
    {
        await _d.DispatchAsync("{not valid json");

        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task PayloadOmitted_HandlerGetsUndefinedElement()
    {
        _d.Register("probe", Handler(p => p.ValueKind.ToString()));

        await _d.DispatchAsync("""{"id":"r","kind":"probe"}""");

        LastEnvelope().GetProperty("payload").GetString().Should().Be("Undefined");
    }

    [Fact]
    public async Task HandlerThrows_ReturnsScrubbedError()
    {
        _d.Register("boom", (_, _) =>
            throw new InvalidOperationException("connect failed: Password=hunter2;Host=h"));

        await _d.DispatchAsync("""{"id":"r","kind":"boom"}""");

        var msg = LastEnvelope().GetProperty("payload").GetProperty("message").GetString()!;
        msg.Should().NotContain("hunter2", "the password must be scrubbed before leaving the process");
        msg.Should().Contain("Password=****");
    }

    [Fact]
    public async Task Register_ReplacesPreviousHandler()
    {
        _d.Register("k", Handler(_ => "first"));
        _d.Register("k", Handler(_ => "second"));

        await _d.DispatchAsync("""{"id":"r","kind":"k"}""");

        LastEnvelope().GetProperty("payload").GetString().Should().Be("second");
    }

    [Fact]
    public async Task SendAsync_EmitsEventEnvelopeWithGeneratedId()
    {
        await _d.SendAsync("notify", new { a = 1 });

        var env = LastEnvelope();
        env.GetProperty("kind").GetString().Should().Be("notify");
        env.GetProperty("payload").GetProperty("a").GetInt32().Should().Be(1);
        env.GetProperty("id").GetString().Should().HaveLength(32);
    }

    [Fact]
    public void Register_RejectsEmptyKindAndNullHandler()
    {
        var register = (string k) => _d.Register(k, Handler(_ => null));
        register.Invoking(r => r(" ")).Should().Throw<ArgumentException>();
        _d.Invoking(d => d.Register("k", null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Clear_RemovesHandlers()
    {
        _d.Register("k", Handler(_ => "x"));
        _d.Clear();

        await _d.DispatchAsync("""{"id":"r","kind":"k"}""");

        LastEnvelope().GetProperty("payload").GetProperty("message").GetString()
            .Should().Contain("No handler registered");
    }
}
