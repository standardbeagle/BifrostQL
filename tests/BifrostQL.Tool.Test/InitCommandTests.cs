using BifrostQL.Tool.Commands;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Tool.Test;

/// <summary>
/// `bifrost init` scaffolds the connection string a new project starts from, and that
/// string is the one most likely to be edited in place and shipped. Pre-setting
/// TrustServerCertificate=True there hands every newly generated project a connection
/// that encrypts without verifying who it is talking to, before the author has made any
/// decision at all.
/// </summary>
public class InitCommandTests
{
    [Fact]
    public void GenerateDefaultConfigJson_DoesNotScaffoldACertificateValidationWaiver()
    {
        // Act
        var json = InitCommand.GenerateDefaultConfigJson();

        // Assert
        json.Should().NotContain("TrustServerCertificate");
    }

    [Fact]
    public void GenerateDefaultConfigJson_StillScaffoldsAUsableConnectionString()
    {
        // Act
        var json = InitCommand.GenerateDefaultConfigJson();

        // Assert
        json.Should().Contain("Server=localhost").And.Contain("Database=mydb");
    }
}
