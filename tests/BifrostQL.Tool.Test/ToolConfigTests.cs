using BifrostQL.Tool.Commands;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Tool.Test;

/// <summary>
/// Enforcing certificate validation is only half a fix if the operator with a genuine
/// self-signed internal server has no way to say so. The waiver has to be reachable from
/// the command line, and it has to survive the implicit-serve rewrite — otherwise
/// `bifrost db appdb --trust-server-certificate` silently drops it on the way to serve.
/// </summary>
public class ToolConfigTests
{
    [Fact]
    public void Parse_DefaultsToValidatingTheServerCertificate()
    {
        // Act
        var config = ToolConfig.Parse(["serve", "db.corp", "appdb"]);

        // Assert
        config.TrustServerCertificate.Should().BeFalse();
    }

    [Fact]
    public void Parse_ReadsTheCertificateTrustWaiver()
    {
        // Act
        var config = ToolConfig.Parse(["serve", "db.corp", "appdb", "--trust-server-certificate"]);

        // Assert
        config.TrustServerCertificate.Should().BeTrue();
        config.CommandArgs.Should().Equal("db.corp", "appdb");
    }

    [Fact]
    public void WithImplicitServe_CarriesTheCertificateTrustWaiver()
    {
        // Arrange
        var config = ToolConfig.Parse(["db.corp", "appdb", "--trust-server-certificate"]);

        // Act
        var implicitServe = config.WithImplicitServe();

        // Assert
        implicitServe.TrustServerCertificate.Should().BeTrue();
        implicitServe.CommandArgs.Should().Equal("db.corp", "appdb");
    }

    [Fact]
    public void Parse_DefaultsHostToLoopback()
    {
        // The serve command ships no auth, so it must bind loopback unless the
        // operator explicitly widens it. The default is fail-secure.
        var config = ToolConfig.Parse(["serve", "db.corp", "appdb"]);

        config.Host.Should().Be("127.0.0.1");
        ServeCommand.IsLoopback(config.Host).Should().BeTrue();
    }

    [Fact]
    public void Parse_ReadsExplicitHostWidening()
    {
        var config = ToolConfig.Parse(["serve", "db.corp", "appdb", "--host", "0.0.0.0"]);

        config.Host.Should().Be("0.0.0.0");
        ServeCommand.IsLoopback(config.Host).Should().BeFalse(
            "0.0.0.0 is a wildcard bind, not loopback — it must trigger the exposure warning");
    }

    [Fact]
    public void WithImplicitServe_CarriesTheHost()
    {
        var config = ToolConfig.Parse(["db.corp", "appdb", "--host", "0.0.0.0"]);

        config.WithImplicitServe().Host.Should().Be("0.0.0.0");
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("example.com", false)]
    public void IsLoopback_ClassifiesHosts(string host, bool expected)
    {
        ServeCommand.IsLoopback(host).Should().Be(expected);
    }
}
