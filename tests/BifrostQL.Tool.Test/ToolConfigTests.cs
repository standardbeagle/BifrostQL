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
}
