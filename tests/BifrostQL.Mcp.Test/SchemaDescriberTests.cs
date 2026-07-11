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
        public void UnknownTableMessage_EmptyModel_ReturnsPromptStyleErrorWithoutThrowing()
        {
            // Arrange: a model exposing no tables (e.g. an empty database or a
            // metadata configuration that hides every table).
            var model = new DbModel
            {
                Tables = Array.Empty<IDbTable>(),
                Metadata = new Dictionary<string, object?>(),
            };

            // Act
            var message = SchemaDescriber.UnknownTableMessage(model, "orders");

            // Assert: prompt-style error, no did-you-mean suggestion, states
            // that no tables are available.
            message.Should().Contain("Unknown table 'orders'");
            message.Should().Contain("No tables are available");
            message.Should().NotContain("Did you mean");
        }
    }
}
