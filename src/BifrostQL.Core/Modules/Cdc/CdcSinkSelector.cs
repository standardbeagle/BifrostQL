using System;

namespace BifrostQL.Core.Modules.Cdc
{
    /// <summary>
    /// Picks the single active <see cref="IEventSink"/> the dispatcher drains to, from the
    /// host's configured sink URLs. Precedence is <b>NATS, then webhook, then none</b>.
    ///
    /// <para>This exists because the dispatcher resolves exactly ONE <see cref="IEventSink"/>,
    /// yet two opt-in sinks (webhook — slice 5, NATS — slice 7) can be wired. Selecting between
    /// them must be a single runtime decision over the two URLs — NOT two independent
    /// <c>TryAddSingleton</c> registrations. <c>TryAdd</c> keys on whether a descriptor is
    /// already present at REGISTRATION time, not on the factory's runtime return, so registering
    /// "NATS then webhook" as two TryAdds silently makes the second a no-op: a webhook-only host
    /// (NATS URL blank) would resolve the NATS factory, get <c>null</c>, and never reach the
    /// webhook factory. Concentrating the choice here keeps that precedence in one tested place.</para>
    ///
    /// <para>Only the chosen sink's builder runs, so the loser never opens a connection or
    /// constructs an <see cref="System.Net.Http.HttpClient"/> — a webhook-only host opens no NATS
    /// connection, a NATS-only host builds no webhook client, and a host configuring neither builds
    /// nothing and the dispatcher idles.</para>
    /// </summary>
    public static class CdcSinkSelector
    {
        /// <summary>
        /// Returns the active sink, or <c>null</c> when neither URL is configured. NATS wins when
        /// its URL is present; otherwise the webhook builder runs when its URL is present; otherwise
        /// no sink. The builder for the sink that is NOT selected is never invoked.
        /// </summary>
        /// <param name="natsUrl">The configured NATS URL (<see cref="NatsEventSink.UrlConfigKey"/>), or blank/absent.</param>
        /// <param name="buildNats">Builds the NATS sink. Invoked ONLY when <paramref name="natsUrl"/> is non-blank.</param>
        /// <param name="webhookUrl">The configured webhook URL (<see cref="WebhookEventSink.EndpointConfigKey"/>), or blank/absent.</param>
        /// <param name="buildWebhook">Builds the webhook sink. Invoked ONLY when NATS is absent and <paramref name="webhookUrl"/> is non-blank.</param>
        public static IEventSink? Select(
            string? natsUrl,
            Func<IEventSink> buildNats,
            string? webhookUrl,
            Func<IEventSink> buildWebhook)
        {
            if (buildNats is null) throw new ArgumentNullException(nameof(buildNats));
            if (buildWebhook is null) throw new ArgumentNullException(nameof(buildWebhook));

            if (!string.IsNullOrWhiteSpace(natsUrl))
                return buildNats();

            if (!string.IsNullOrWhiteSpace(webhookUrl))
                return buildWebhook();

            return null;
        }
    }
}
