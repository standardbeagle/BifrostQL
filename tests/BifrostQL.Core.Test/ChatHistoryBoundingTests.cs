using System;
using BifrostQL.Core.Modules.Chat;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test
{
    /// <summary>
    /// Pins the pure history-bounding rule the chat endpoints rely on: the window
    /// addresses the LAST N messages of a chronologically-sorted conversation, so an
    /// over-long conversation is truncated from the oldest side.
    /// </summary>
    public sealed class ChatHistoryBoundingTests
    {
        [Theory]
        [InlineData(0, 50, 0)]    // empty conversation
        [InlineData(10, 50, 0)]   // under the window: everything is sent
        [InlineData(50, 50, 0)]   // exactly the window
        [InlineData(51, 50, 1)]   // one over: the single oldest message drops
        [InlineData(500, 50, 450)]
        [InlineData(5, 1, 4)]     // window of one keeps only the newest
        public void LastMessagesWindow_AddressesTheNewestNMessages(int totalCount, int limit, int expectedOffset)
        {
            var page = ChatConversationStore.LastMessagesWindow(totalCount, limit);

            page.Limit.Should().Be(limit);
            page.Offset.Should().Be(expectedOffset);
        }

        [Fact]
        public void LastMessagesWindow_RejectsInvalidInputs_FailFast()
        {
            var negativeTotal = () => ChatConversationStore.LastMessagesWindow(-1, 50);
            var zeroLimit = () => ChatConversationStore.LastMessagesWindow(10, 0);

            negativeTotal.Should().Throw<ArgumentOutOfRangeException>();
            zeroLimit.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
