using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.UI;
using BifrostQL.UI.Web;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Pins the startup-failure contract that the desktop entry point depends on.
///
/// Observed live: with port 5000 already bound, Kestrel threw
/// <c>AddressInUseException</c> and the entry point carried on into
/// <c>DesktopShell.RunAsync</c>, which resolved <c>app.Services</c> on a host that
/// had already been torn down — an <c>ObjectDisposedException</c> stack trace that
/// buried the real diagnosis. Startup must therefore be a decision the caller can
/// branch on BEFORE anything resolves services.
/// </summary>
public sealed class HostStartupTests
{
    [Fact]
    public async Task TryStartAsync_reports_the_port_actionably_when_it_is_already_bound()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var state = new ConnectionState();
            await using var sshTunnel = new SshTunnelManager();
            var app = BifrostUiWebHost.Build(null, port, state, sshTunnel);

            // Act — must surface the bind failure as a message, never as a throw,
            // and never leave a half-started host whose provider is disposed.
            var failure = await HostStartup.TryStartAsync(app, port, CancellationToken.None);

            // Assert
            failure.Should().NotBeNull("port {0} was already bound", port);
            failure.Should().Contain(port.ToString(), "the operator needs to know which port");
            failure.Should().Contain("--port", "the message must point at the escape hatch");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TryStartAsync_does_not_surface_ObjectDisposedException_for_a_bound_port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var state = new ConnectionState();
            await using var sshTunnel = new SshTunnelManager();
            var app = BifrostUiWebHost.Build(null, port, state, sshTunnel);

            var failure = await HostStartup.TryStartAsync(app, port, CancellationToken.None);

            // The secondary ObjectDisposedException is exactly what masked the real
            // cause on screen; the message must name the bind, not the provider.
            failure.Should().NotBeNull();
            failure.Should().NotContain("disposed", "the real cause must not be masked");
            failure.Should().NotContain("IServiceProvider");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TryStartAsync_returns_null_when_the_port_is_free()
    {
        var port = FreePort();
        var state = new ConnectionState();
        await using var sshTunnel = new SshTunnelManager();
        var app = BifrostUiWebHost.Build(null, port, state, sshTunnel);

        var failure = await HostStartup.TryStartAsync(app, port, CancellationToken.None);

        try
        {
            failure.Should().BeNull();
            // The provider is live on the success path — the caller may resolve services.
            app.Services.GetService(typeof(ILoggerFactory)).Should().NotBeNull();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(SocketError.AddressAlreadyInUse, true)]
    [InlineData(SocketError.AccessDenied, false)]
    public void IsAddressInUse_unwraps_nested_socket_errors(SocketError error, bool expected)
    {
        var wrapped = new InvalidOperationException(
            "outer", new AggregateException(new SocketException((int)error)));

        HostStartup.IsAddressInUse(wrapped).Should().Be(expected);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
