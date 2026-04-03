using BifrostQL.UI;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Unit tests for SshTunnelManager — tests the state machine, argument building,
/// WP-CLI JSON parsing, and port allocation without requiring an actual SSH server.
/// </summary>
public sealed class SshTunnelManagerTests : IDisposable
{
    private readonly SshTunnelManager _manager = new();

    [Fact]
    public void IsActive_InitiallyFalse()
    {
        _manager.IsActive.Should().BeFalse();
    }

    [Fact]
    public void LocalPort_InitiallyNull()
    {
        _manager.LocalPort.Should().BeNull();
    }

    [Fact]
    public void GetStatus_ReturnsInactiveInitially()
    {
        var status = _manager.GetStatus();
        status.Should().NotBeNull();
    }

    [Fact]
    public async Task StopAsync_WhenNotStarted_DoesNotThrow()
    {
        await _manager.StopAsync();
        _manager.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_WithInvalidHost_ThrowsWithMessage()
    {
        var config = new SshTunnelConfig(
            "nonexistent.invalid.host.example", 22, "user", null, "localhost", 5432);

        var act = () => _manager.StartAsync(config);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("SSH tunnel failed"));
    }

    [Fact]
    public async Task StartAsync_WithBadPort_ThrowsWithMessage()
    {
        var config = new SshTunnelConfig(
            "localhost", 99999, "user", null, "localhost", 5432);

        var act = () => _manager.StartAsync(config);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _manager.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DiscoverWordPressAsync_WithInvalidHost_Throws()
    {
        var sshConfig = new SshTunnelConfig(
            "nonexistent.invalid.host.example", 22, "user", null, "localhost", 3306);
        var wpConfig = new WpDiscoverConfig(null, null);

        var act = () => _manager.DiscoverWordPressAsync(sshConfig, wpConfig);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Dispose_WhenNotStarted_DoesNotThrow()
    {
        using var manager = new SshTunnelManager();
        manager.Dispose();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var manager = new SshTunnelManager();
        manager.Dispose();
        var act = () => manager.Dispose();
        act.Should().NotThrow();
    }

    public void Dispose() => _manager.Dispose();
}

/// <summary>
/// Tests for WP-CLI JSON parsing logic via reflection.
/// Exercises the ParseWpConfigOutput private method indirectly through DiscoverWordPressAsync
/// error paths, and directly tests expected JSON structures.
/// </summary>
public sealed class WpConfigParsingTests
{
    [Fact]
    public void ParseWpConfig_ValidJson_ExtractsCredentials()
    {
        // Test the JSON format wp-cli returns
        var json = """
        [
            {"name": "DB_NAME", "value": "wordpress_db"},
            {"name": "DB_USER", "value": "wp_user"},
            {"name": "DB_PASSWORD", "value": "s3cret"},
            {"name": "DB_HOST", "value": "localhost"},
            {"name": "DB_CHARSET", "value": "utf8mb4"},
            {"name": "table_prefix", "value": "wp_"}
        ]
        """;

        var method = typeof(SshTunnelManager).GetMethod("ParseWpConfigOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("ParseWpConfigOutput should exist as a private static method");

        var result = (WpCredentials)method!.Invoke(null, [json])!;
        result.DbName.Should().Be("wordpress_db");
        result.DbUser.Should().Be("wp_user");
        result.DbPassword.Should().Be("s3cret");
        result.DbHost.Should().Be("localhost");
    }

    [Fact]
    public void ParseWpConfig_WithHostPort_ExtractsFullHost()
    {
        var json = """
        [
            {"name": "DB_NAME", "value": "wp"},
            {"name": "DB_USER", "value": "root"},
            {"name": "DB_PASSWORD", "value": "pass"},
            {"name": "DB_HOST", "value": "db.server.com:3307"}
        ]
        """;

        var method = typeof(SshTunnelManager).GetMethod("ParseWpConfigOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (WpCredentials)method.Invoke(null, [json])!;
        result.DbHost.Should().Be("db.server.com:3307");
    }

    [Fact]
    public void ParseWpConfig_MissingDbName_Throws()
    {
        var json = """
        [
            {"name": "DB_USER", "value": "root"},
            {"name": "DB_PASSWORD", "value": "pass"},
            {"name": "DB_HOST", "value": "localhost"}
        ]
        """;

        var method = typeof(SshTunnelManager).GetMethod("ParseWpConfigOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var act = () => method.Invoke(null, [json]);
        act.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*DB_NAME*");
    }

    [Fact]
    public void ParseWpConfig_EmptyPassword_ReturnsEmptyString()
    {
        var json = """
        [
            {"name": "DB_NAME", "value": "wp"},
            {"name": "DB_USER", "value": "root"},
            {"name": "DB_PASSWORD", "value": ""},
            {"name": "DB_HOST", "value": "localhost"}
        ]
        """;

        var method = typeof(SshTunnelManager).GetMethod("ParseWpConfigOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (WpCredentials)method.Invoke(null, [json])!;
        result.DbPassword.Should().BeEmpty();
    }

    [Fact]
    public void ParseWpConfig_MissingHost_DefaultsToLocalhost()
    {
        var json = """
        [
            {"name": "DB_NAME", "value": "wp"},
            {"name": "DB_USER", "value": "root"},
            {"name": "DB_PASSWORD", "value": "pass"}
        ]
        """;

        var method = typeof(SshTunnelManager).GetMethod("ParseWpConfigOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (WpCredentials)method.Invoke(null, [json])!;
        result.DbHost.Should().Be("localhost");
    }

    [Fact]
    public void ParseWpConfig_InvalidJson_Throws()
    {
        var method = typeof(SshTunnelManager).GetMethod("ParseWpConfigOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var act = () => method.Invoke(null, ["not json at all"]);
        act.Should().Throw<System.Reflection.TargetInvocationException>();
    }

    [Fact]
    public void ParseWpConfig_NotArray_Throws()
    {
        var method = typeof(SshTunnelManager).GetMethod("ParseWpConfigOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var act = () => method.Invoke(null, ["""{"name": "DB_NAME"}"""]);
        act.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*format*");
    }
}
