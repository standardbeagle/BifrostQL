using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    public sealed class ServerSentEventsTests
    {
        [Fact]
        public void Format_EmitsNamedEvent_WithDataLine_AndBlankTerminator()
        {
            ServerSentEvents.Format("delta", "{\"text\":\"hi\"}")
                .Should().Be("event: delta\ndata: {\"text\":\"hi\"}\n\n");
        }

        [Fact]
        public void Format_SplitsMultiLinePayloads_AcrossDataLines_SoNoPrematureTerminatorLeaks()
        {
            // Raw newlines in a payload would otherwise terminate the event early;
            // the SSE encoding for embedded newlines is one data: line per line.
            ServerSentEvents.Format("delta", "line-1\nline-2\r\nline-3")
                .Should().Be("event: delta\ndata: line-1\ndata: line-2\ndata: line-3\n\n");
        }

        [Fact]
        public void Format_PreservesEmptyData_AsASingleEmptyDataLine()
        {
            ServerSentEvents.Format("done", string.Empty)
                .Should().Be("event: done\ndata: \n\n");
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("two\nlines")]
        [InlineData("carriage\rreturn")]
        public void Format_RejectsInvalidEventNames(string eventName)
        {
            var act = () => ServerSentEvents.Format(eventName, "{}");
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Format_RejectsNullData()
        {
            var act = () => ServerSentEvents.Format("delta", null!);
            act.Should().Throw<ArgumentNullException>();
        }
    }
}
