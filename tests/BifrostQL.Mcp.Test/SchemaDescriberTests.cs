using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// Unit tests for <see cref="SchemaDescriber"/> edge cases that the
    /// end-to-end fixture (which always has tables) cannot reach.
    /// </summary>
    public sealed class SchemaDescriberTests
    {
        [Fact]
        public void UnknownTableMessage_NoVisibleTables_ReturnsPromptStyleErrorWithoutThrowing()
        {
            // Arrange: no table is visible to this caller — an empty database, or a
            // schema in which policy denies the caller every table. Both collapse to the
            // same projection, which is the point: the message cannot distinguish them.
            var visible = Array.Empty<VisibleTable>();

            // Act
            var message = SchemaDescriber.UnknownTableMessage(visible, "orders");

            // Assert: prompt-style error, no did-you-mean suggestion, states
            // that no tables are available.
            message.Should().Contain("Unknown table 'orders'");
            message.Should().Contain("No tables are available");
            message.Should().NotContain("Did you mean");
        }
    }
}
