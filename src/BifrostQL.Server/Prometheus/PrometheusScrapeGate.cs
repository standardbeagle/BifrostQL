using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// The credential gate for the Prometheus business-metrics scrape surface — the FIRST thing a
    /// scrape handler consults, before any model lookup or aggregate collection (fail-closed by
    /// construction). It compares the presented credential against the configured
    /// <see cref="PrometheusScrapeSecurityOptions.ScrapeCredential"/> and answers a single boolean:
    /// authorized or not. It exposes NO reason — absent, wrong, and disabled all return the same
    /// <c>false</c>, so a caller renders one uniform denial with no oracle distinguishing them
    /// (see .claude/rules/protocol-adapter-security.md invariants 2 and 3).
    ///
    /// <para>Security posture:</para>
    /// <list type="bullet">
    /// <item>The constant-time compare (<see cref="CryptographicOperations.FixedTimeEquals"/>) runs
    /// UNCONDITIONALLY against a real or decoy digest — never gated behind a null/length/enabled
    /// check — and the armed/existence check is ANDed only AFTER it, so a disarmed surface does the
    /// same work as an armed one with a wrong credential (invariant 2).</item>
    /// <item>Both sides are SHA-256 digested first, so the compare is fixed-length and the
    /// credential's length is never leaked.</item>
    /// <item>The secret is never logged. Arming the surface logs a WARNING posture note (an enabled
    /// cross-tenant/service-identity exposure is a posture change worth surfacing) — the note
    /// carries no credential material.</item>
    /// </list>
    /// </summary>
    public sealed class PrometheusScrapeGate
    {
        // A fixed, non-secret decoy that keeps the compare work identical when the surface is
        // disarmed. Its only requirement is that a real scraper cannot know it, which holds because
        // it never leaves the process and no credential is provisioned with it.
        private const string DecoySecret = "bifrost-prometheus-decoy-secret-not-a-real-credential";

        private readonly byte[] _expectedDigest;
        private readonly bool _armed;

        public PrometheusScrapeGate(PrometheusScrapeSecurityOptions options, ILogger<PrometheusScrapeGate>? logger = null)
        {
            ArgumentNullException.ThrowIfNull(options);

            _armed = options.IsArmed;

            // Digest the real credential when armed, otherwise a decoy — so the runtime compare
            // always runs against real bytes regardless of whether a credential is configured.
            var secret = _armed ? options.ScrapeCredential! : DecoySecret;
            _expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(secret));

            if (_armed)
                logger?.LogWarning(
                    "Prometheus business metrics ENABLED: a scrape credential is configured and business-metric " +
                    "series will be exported to any scraper presenting it. This is an explicit cross-tenant / " +
                    "service-identity exposure posture change.");
        }

        /// <summary>
        /// Whether <paramref name="presentedCredential"/> authorizes the scrape. The constant-time
        /// compare runs unconditionally FIRST; the armed check is ANDed after. Returns the same
        /// <c>false</c> whether the credential is absent, wrong, or the surface is disarmed — no
        /// distinguishing signal (no throw, no reason).
        /// </summary>
        public bool IsAuthorized(string? presentedCredential)
        {
            // Unconditional constant-time compare — NEVER short-circuited on the null/enabled check.
            var presented = presentedCredential ?? string.Empty;
            var matches = CryptographicOperations.FixedTimeEquals(
                _expectedDigest,
                SHA256.HashData(Encoding.UTF8.GetBytes(presented)));

            // AND the armed/existence check only AFTER the compare has run (invariant 2).
            return matches && _armed;
        }
    }
}
