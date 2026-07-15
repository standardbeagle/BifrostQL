using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Modules.Cdc;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for <see cref="CdcSinkSelector"/> — the single runtime choice between the two opt-in
/// CDC sinks (NATS — slice 7, webhook — slice 5). Regression guard for the two-<c>TryAddSingleton</c>
/// bug: registering "NATS then webhook" as two TryAdds made the webhook a no-op, so a webhook-only
/// host silently resolved a null sink. The selector concentrates the precedence (NATS → webhook →
/// none) in one place and builds ONLY the chosen sink, so the loser opens no connection.
/// </summary>
public class CdcSinkSelectorTests
{
    // A trivial IEventSink stand-in — the selector never calls it, it only needs an identity.
    private sealed class StubSink : IEventSink
    {
        public ValueTask<EventDeliveryResult> DeliverAsync(
            JsonObject envelope, string idempotencyKey, CancellationToken cancellationToken)
            => new(EventDeliveryResult.Delivered);
    }

    [Fact]
    public void Nats_url_present_selects_nats_and_never_builds_the_webhook()
    {
        // Arrange
        var nats = new StubSink();
        var natsBuilt = false;
        var webhookBuilt = false;

        // Act — both URLs present: NATS must win, and the webhook builder must NOT run (no wasted
        // HttpClient, and the precedence is deterministic).
        var chosen = CdcSinkSelector.Select(
            natsUrl: "nats://localhost:4222",
            buildNats: () => { natsBuilt = true; return nats; },
            webhookUrl: "https://hooks.example/cdc",
            buildWebhook: () => { webhookBuilt = true; return new StubSink(); });

        // Assert
        chosen.Should().BeSameAs(nats);
        natsBuilt.Should().BeTrue();
        webhookBuilt.Should().BeFalse("NATS wins, so the webhook sink is never constructed");
    }

    [Fact]
    public void Nats_only_selects_nats_and_never_builds_the_webhook()
    {
        var nats = new StubSink();
        var natsBuilt = false;
        var webhookBuilt = false;

        var chosen = CdcSinkSelector.Select(
            natsUrl: "nats://localhost:4222",
            buildNats: () => { natsBuilt = true; return nats; },
            webhookUrl: null,
            buildWebhook: () => { webhookBuilt = true; return new StubSink(); });

        chosen.Should().BeSameAs(nats, "a NATS-only host must resolve the NATS sink");
        natsBuilt.Should().BeTrue();
        webhookBuilt.Should().BeFalse("no webhook URL, so no HttpClient is constructed");
    }

    [Fact]
    public void Webhook_only_selects_webhook_and_never_builds_nats()
    {
        // This is the exact case the two-TryAdd bug broke: NATS URL blank, webhook URL set.
        var webhook = new StubSink();
        var natsBuilt = false;
        var webhookBuilt = false;

        var chosen = CdcSinkSelector.Select(
            natsUrl: "   ",
            buildNats: () => { natsBuilt = true; return new StubSink(); },
            webhookUrl: "https://hooks.example/cdc",
            buildWebhook: () => { webhookBuilt = true; return webhook; });

        chosen.Should().BeSameAs(webhook, "a webhook-only host must resolve the webhook sink");
        webhookBuilt.Should().BeTrue();
        natsBuilt.Should().BeFalse("no NATS URL, so no NATS connection is opened");
    }

    [Fact]
    public void Neither_url_selects_no_sink_and_builds_nothing()
    {
        var natsBuilt = false;
        var webhookBuilt = false;

        var chosen = CdcSinkSelector.Select(
            natsUrl: null,
            buildNats: () => { natsBuilt = true; return new StubSink(); },
            webhookUrl: "",
            buildWebhook: () => { webhookBuilt = true; return new StubSink(); });

        chosen.Should().BeNull("a host configuring neither sink pays nothing and the dispatcher idles");
        natsBuilt.Should().BeFalse();
        webhookBuilt.Should().BeFalse();
    }
}
