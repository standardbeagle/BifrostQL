using System;
using System.Collections.Generic;
using BifrostQL.Server.Prometheus;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BifrostQL.Server.Test.Prometheus
{
    /// <summary>
    /// The scrape credential gate (slice 3, criterion 2 + invariants 2/3). Business metrics are OFF
    /// by default; enabling requires BOTH the flag and a configured credential. The gate compares in
    /// constant time, runs the compare UNCONDITIONALLY, never logs the secret, and returns a uniform
    /// denial with no oracle distinguishing absent / wrong / disabled.
    /// </summary>
    public sealed class PrometheusScrapeGateTests
    {
        private const string Secret = "s3cr3t-scrape-token-value";

        private static PrometheusScrapeGate Armed(ILogger<PrometheusScrapeGate>? logger = null) =>
            new(new PrometheusScrapeSecurityOptions
            {
                BusinessMetricsEnabled = true,
                ScrapeCredential = Secret,
            }, logger);

        // ---- default OFF -------------------------------------------------------------------

        [Fact]
        public void Business_metrics_are_off_by_default()
        {
            var options = new PrometheusScrapeSecurityOptions();

            options.BusinessMetricsEnabled.Should().BeFalse();
            options.IsArmed.Should().BeFalse();

            // Even presenting the "right" value denies, because the surface is not enabled.
            var gate = new PrometheusScrapeGate(options);
            gate.IsAuthorized(Secret).Should().BeFalse();
        }

        [Fact]
        public void Enabled_without_a_credential_is_disarmed()
        {
            var gate = new PrometheusScrapeGate(new PrometheusScrapeSecurityOptions
            {
                BusinessMetricsEnabled = true,
                ScrapeCredential = null,
            });

            gate.IsAuthorized(null).Should().BeFalse();
            gate.IsAuthorized("anything").Should().BeFalse();
        }

        [Fact]
        public void Credential_without_the_enable_flag_is_disarmed()
        {
            var gate = new PrometheusScrapeGate(new PrometheusScrapeSecurityOptions
            {
                BusinessMetricsEnabled = false,
                ScrapeCredential = Secret,
            });

            gate.IsAuthorized(Secret).Should().BeFalse();
        }

        // ---- correct credential proceeds ---------------------------------------------------

        [Fact]
        public void The_correct_credential_authorizes_when_armed()
        {
            Armed().IsAuthorized(Secret).Should().BeTrue();
        }

        // ---- uniform denial: no oracle -----------------------------------------------------

        [Fact]
        public void Absent_wrong_and_disabled_all_deny_identically()
        {
            var armed = Armed();
            var disabled = new PrometheusScrapeGate(new PrometheusScrapeSecurityOptions
            {
                BusinessMetricsEnabled = false,
                ScrapeCredential = Secret,
            });

            // Absent credential, wrong credential (armed), and a disabled surface all yield the SAME
            // boolean decision — false — with no exception and no distinguishing signal.
            var absent = armed.IsAuthorized(null);
            var wrong = armed.IsAuthorized("not-the-secret");
            var wrongLength = armed.IsAuthorized("x"); // different length must not be an oracle
            var disabledCorrect = disabled.IsAuthorized(Secret);

            absent.Should().BeFalse();
            wrong.Should().BeFalse();
            wrongLength.Should().BeFalse();
            disabledCorrect.Should().BeFalse();

            new[] { absent, wrong, wrongLength, disabledCorrect }.Should().AllBeEquivalentTo(false);
        }

        [Fact]
        public void An_empty_presented_credential_denies_and_does_not_throw()
        {
            var gate = Armed();
            gate.IsAuthorized("").Should().BeFalse();
            // Even when the surface is disarmed (decoy path), an empty/absent credential is safe.
            var disarmed = new PrometheusScrapeGate(new PrometheusScrapeSecurityOptions());
            disarmed.IsAuthorized("").Should().BeFalse();
            disarmed.IsAuthorized(null).Should().BeFalse();
        }

        // ---- secret is never logged; posture note fires on enable --------------------------

        [Fact]
        public void Arming_logs_a_posture_warning_without_the_secret()
        {
            var logger = new CapturingLogger<PrometheusScrapeGate>();
            _ = Armed(logger);

            logger.Warnings.Should().ContainSingle();
            logger.Warnings[0].Should().Contain("ENABLED");
            // The credential material must never appear in any log line.
            logger.Warnings[0].Should().NotContain(Secret);
        }

        [Fact]
        public void A_disarmed_surface_logs_no_posture_note()
        {
            var logger = new CapturingLogger<PrometheusScrapeGate>();
            _ = new PrometheusScrapeGate(new PrometheusScrapeSecurityOptions(), logger);

            logger.Warnings.Should().BeEmpty();
        }

        [Fact]
        public void The_secret_never_appears_in_any_log_regardless_of_presented_value()
        {
            var logger = new CapturingLogger<PrometheusScrapeGate>();
            var gate = Armed(logger);

            // Exercising the compare path emits no logs at all (and certainly not the secret).
            gate.IsAuthorized(Secret);
            gate.IsAuthorized("wrong");

            logger.Warnings.Should().OnlyContain(w => !w.Contains(Secret));
        }

        private sealed class CapturingLogger<T> : ILogger<T>
        {
            public List<string> Warnings { get; } = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                    Warnings.Add(formatter(state, exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
