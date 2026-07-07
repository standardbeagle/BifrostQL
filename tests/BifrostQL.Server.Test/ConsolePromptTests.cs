using System.Text;
using BifrostQL.Server;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// Tests for <see cref="ConsolePrompt.ReadPassword"/>.
/// Only the redirected-input path is exercised: <see cref="Console.ReadKey"/> requires an
/// interactive console and cannot be driven from a test harness, whereas redirected stdin
/// (the case that runs under a test runner and in pipelines) is deterministic.
/// </summary>
[Collection("ConsoleRedirect")]
public class ConsolePromptTests
{
    [Fact]
    public void ReadPassword_RedirectedInput_ReturnsLine()
    {
        // Arrange
        var originalIn = Console.In;
        var originalOut = Console.Out;
        try
        {
            Console.SetIn(new StringReader("hunter2\n"));
            var output = new StringWriter();
            Console.SetOut(output);

            // Act
            var result = ConsolePrompt.ReadPassword("Password: ");

            // Assert
            result.Should().Be("hunter2");
            // Prompt is written but the password is never echoed on the redirected path.
            output.ToString().Should().Be("Password: ");
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void ReadPassword_RedirectedEmptyInput_ReturnsEmptyString()
    {
        // Arrange
        var originalIn = Console.In;
        var originalOut = Console.Out;
        try
        {
            // No newline / EOF immediately => ReadLine returns null => coalesced to "".
            Console.SetIn(new StringReader(""));
            Console.SetOut(new StringWriter());

            // Act
            var result = ConsolePrompt.ReadPassword("Password: ");

            // Assert
            result.Should().BeEmpty();
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }
}
