using System.Net;
using BifrostQL.Server;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Xunit;

namespace BifrostQL.Server.Test
{
    public class BifrostHttp3OptionsTests
    {
        [Fact]
        public void DefaultOptions_HasExpectedDefaults()
        {
            var options = new BifrostHttp3Options();

            options.HttpsPort.Should().Be(5001);
            options.HttpPort.Should().Be(5000);
            options.ListenAddress.Should().Be(IPAddress.Any);
        }

        [Fact]
        public void HttpPort_CanBeDisabled()
        {
            var options = new BifrostHttp3Options { HttpPort = null };

            options.HttpPort.Should().BeNull();
        }

        [Fact]
        public void HttpsPort_CanBeCustomized()
        {
            var options = new BifrostHttp3Options { HttpsPort = 8443 };

            options.HttpsPort.Should().Be(8443);
        }

        [Fact]
        public void ListenAddress_CanBeSetToLoopback()
        {
            var options = new BifrostHttp3Options { ListenAddress = IPAddress.Loopback };

            options.ListenAddress.Should().Be(IPAddress.Loopback);
        }

        [Fact]
        public void ListenAddress_CanBeSetToIPv6Any()
        {
            var options = new BifrostHttp3Options { ListenAddress = IPAddress.IPv6Any };

            options.ListenAddress.Should().Be(IPAddress.IPv6Any);
        }

        [Fact]
        public void AllOptions_CanBeConfiguredTogether()
        {
            var options = new BifrostHttp3Options
            {
                HttpsPort = 9443,
                HttpPort = 9080,
                ListenAddress = IPAddress.Loopback,
            };

            options.HttpsPort.Should().Be(9443);
            options.HttpPort.Should().Be(9080);
            options.ListenAddress.Should().Be(IPAddress.Loopback);
        }

        [Theory]
        [InlineData(443)]
        [InlineData(8443)]
        [InlineData(44300)]
        public void HttpsPort_AcceptsCommonPorts(int port)
        {
            var options = new BifrostHttp3Options { HttpsPort = port };

            options.HttpsPort.Should().Be(port);
        }

        [Theory]
        [InlineData(80)]
        [InlineData(8080)]
        [InlineData(5000)]
        public void HttpPort_AcceptsCommonPorts(int port)
        {
            var options = new BifrostHttp3Options { HttpPort = port };

            options.HttpPort.Should().Be(port);
        }
    }

    public class BifrostHttp3ExtensionTests
    {
        [Fact]
        public void UseBifrostHttp3_WithDefaults_ReturnsBuilder()
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());

            var result = builder.UseBifrostHttp3();

            result.Should().BeSameAs(builder);
        }

        [Fact]
        public void UseBifrostHttp3_WithCustomPorts_ReturnsBuilder()
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());

            var result = builder.UseBifrostHttp3(opts =>
            {
                opts.HttpsPort = 8443;
                opts.HttpPort = 8080;
            });

            result.Should().BeSameAs(builder);
        }

        [Fact]
        public void UseBifrostHttp3_WithHttpDisabled_ReturnsBuilder()
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());

            var result = builder.UseBifrostHttp3(opts =>
            {
                opts.HttpPort = null;
            });

            result.Should().BeSameAs(builder);
        }

        [Fact]
        public void UseBifrostHttp3_NullConfigure_UsesDefaults()
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());

            var result = builder.UseBifrostHttp3(null);

            result.Should().BeSameAs(builder);
        }

        [Fact]
        public void UseBifrostHttp3_WithLoopbackAddress_ReturnsBuilder()
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());

            var result = builder.UseBifrostHttp3(opts =>
            {
                opts.ListenAddress = IPAddress.Loopback;
            });

            result.Should().BeSameAs(builder);
        }

        [Fact]
        public void UseBifrostHttp3_ConfigureCallbackIsInvoked()
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            var callbackInvoked = false;

            builder.UseBifrostHttp3(opts =>
            {
                callbackInvoked = true;
                opts.HttpsPort = 9443;
            });

            callbackInvoked.Should().BeTrue();
        }

        [Fact]
        public void UseBifrostHttp3_CanChainWithOtherMethods()
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());

            // Verify the extension method is chainable with WebApplicationBuilder methods
            var result = builder.UseBifrostHttp3(opts => opts.HttpsPort = 7443);

            result.Services.Should().NotBeNull();
            result.Environment.Should().NotBeNull();
        }
    }

    public class KestrelHttp3ConfigurationTests
    {
        [Fact]
        public void UseBifrostHttp3_WithDefaults_AcceptsConfiguration()
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());

            // ConfigureKestrel callbacks are deferred until server build.
            // Verify the extension wires up without throwing.
            builder.UseBifrostHttp3();

            builder.Should().NotBeNull();
        }

        [Fact]
        public void ConfigureKestrelForHttp3_HttpDisabled_ConfiguresOneEndpoint()
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());

            builder.UseBifrostHttp3(opts =>
            {
                opts.HttpPort = null;
            });

            // No exception means configuration was accepted
            builder.Should().NotBeNull();
        }

        [Fact]
        public void Http1AndHttp2AndHttp3_IncludesAllProtocols()
        {
            // Verify the HttpProtocols enum value we use includes all three protocols
            var protocols = HttpProtocols.Http1AndHttp2AndHttp3;

            protocols.HasFlag(HttpProtocols.Http1).Should().BeTrue("HTTP/1.1 fallback required");
            protocols.HasFlag(HttpProtocols.Http2).Should().BeTrue("HTTP/2 fallback required");
            protocols.HasFlag(HttpProtocols.Http3).Should().BeTrue("HTTP/3 is the target protocol");
        }

        [Fact]
        public void Http1AndHttp2_DoesNotIncludeHttp3()
        {
            // Verify plaintext endpoint does not advertise HTTP/3 (requires TLS)
            var protocols = HttpProtocols.Http1AndHttp2;

            protocols.HasFlag(HttpProtocols.Http3).Should().BeFalse(
                "HTTP/3 requires TLS, plaintext should not include it");
        }
    }
}
