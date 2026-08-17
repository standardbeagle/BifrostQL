using BifrostQL.Server.Ldap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Startup-time configuration of the LDAP front door's confidential surfaces. Every failure mode
    /// here aborts registration: a listener the operator meant to be confidential must never come up
    /// as a cleartext one, so there is no "TLS could not be configured, continuing" path to test —
    /// only the absence of one.
    /// </summary>
    public sealed class LdapTlsConfigurationTests
    {
        private static ServiceCollection Services() => new();

        [Fact]
        public void LdapsListener_IsDisabledByDefault()
        {
            var options = new LdapWireOptions();

            options.LdapsPort.Should().BeNull("a second open port is never an ambient default");
            options.ServerCertificate.Should().BeNull();
            options.TlsCertificatePath.Should().BeNull();
        }

        [Fact]
        public void LdapsListener_BindsTheSameDeclaredAddress_AsTheCleartextListener()
        {
            // One posture covers both ports: LDAPS does not get its own, wider, bind address.
            var options = new LdapWireOptions { LdapsPort = 636 };

            options.BindAddress.Should().Be(System.Net.IPAddress.Loopback);
        }

        [Fact]
        public void LdapsPort_WithNoCertificate_FailsStartup()
        {
            var act = () => Services().AddBifrostLdap(o =>
            {
                o.Port = 3899;
                o.LdapsPort = 3636;
            });

            act.Should().Throw<LdapConfigurationException>()
                .WithMessage("*LdapsPort*")
                .WithMessage("*no TLS certificate*",
                    "an LDAPS port with nothing to present must not fall back to a cleartext listener");
        }

        [Fact]
        public void MissingCertificateFile_FailsStartup()
        {
            var absent = Path.Combine(Path.GetTempPath(), $"bifrost-ldap-absent-{Guid.NewGuid():N}.pfx");

            var act = () => Services().AddBifrostLdap(o =>
            {
                o.Port = 3899;
                o.TlsCertificatePath = absent;
            });

            act.Should().Throw<LdapConfigurationException>().WithMessage("*could not be loaded*");
        }

        [Fact]
        public async Task CorruptCertificateFile_FailsStartup()
        {
            var corrupt = Path.Combine(Path.GetTempPath(), $"bifrost-ldap-corrupt-{Guid.NewGuid():N}.pfx");
            await File.WriteAllBytesAsync(corrupt, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });
            try
            {
                var act = () => Services().AddBifrostLdap(o =>
                {
                    o.Port = 3899;
                    o.TlsCertificatePath = corrupt;
                });

                act.Should().Throw<LdapConfigurationException>().WithMessage("*could not be loaded*");
            }
            finally
            {
                File.Delete(corrupt);
            }
        }

        [Fact]
        public void CertificateWithoutAPrivateKey_FailsStartup()
        {
            // A public certificate alone can never complete a handshake; catching it at startup beats
            // discovering it on the first client connection.
            var publicOnly = LdapTestCertificate.WithoutPrivateKey();

            var act = () => Services().AddBifrostLdap(o =>
            {
                o.Port = 3899;
                o.ServerCertificate = publicOnly;
            });

            act.Should().Throw<LdapConfigurationException>().WithMessage("*no private key*");
        }

        [Fact]
        public void LdapsPort_EqualToTheCleartextPort_FailsStartup()
        {
            var act = () => Services().AddBifrostLdap(o =>
            {
                o.Port = 3899;
                o.LdapsPort = 3899;
                o.ServerCertificate = LdapTestCertificate.Instance;
            });

            act.Should().Throw<LdapConfigurationException>().WithMessage("*cannot share a port*");
        }

        [Fact]
        public void NonPositiveTlsHandshakeTimeout_FailsStartup()
        {
            var act = () => Services().AddBifrostLdap(o =>
            {
                o.Port = 3899;
                o.TlsHandshakeTimeout = TimeSpan.Zero;
            });

            act.Should().Throw<ArgumentOutOfRangeException>()
                .WithMessage("*TlsHandshakeTimeout*",
                    "an unbounded handshake would let a stalled peer hold its admission slot");
        }

        [Fact]
        public void ConfiguredLdapsPort_ComposesAnLdapsHandler_SharingTheFrontDoorsConnectionCap()
        {
            var services = Services();
            services.AddBifrostLdap(o =>
            {
                o.Port = 3899;
                o.LdapsPort = 3636;
                o.ServerCertificate = LdapTestCertificate.Instance;
            });
            using var provider = services.BuildServiceProvider();

            provider.GetService<LdapsConnectionHandler>().Should().NotBeNull();
            services.Count(d => d.ServiceType == typeof(LdapBoundedCounter)).Should().Be(1,
                "both listeners draw on ONE counter, so opening LDAPS does not double MaxConnections");
        }

        [Fact]
        public void NoLdapsPort_ComposesNoLdapsHandler()
        {
            var services = Services();
            services.AddBifrostLdap(o =>
            {
                o.Port = 3899;
                o.ServerCertificate = LdapTestCertificate.Instance;
            });
            using var provider = services.BuildServiceProvider();

            provider.GetService<LdapsConnectionHandler>().Should().BeNull(
                "a certificate enables StartTLS; the second port is a separate, explicit decision");
        }
    }
}
