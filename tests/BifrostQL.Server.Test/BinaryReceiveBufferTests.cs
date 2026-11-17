using System.Net.WebSockets;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The binary transport receives into a pooled 64 KiB buffer. A message that fits one receive
/// is parsed from that buffer without a copy; a longer one accumulates in a pooled stream up to
/// the 4 MiB message cap, which is checked before each fragment is kept.
/// </summary>
public sealed class BinaryReceiveBufferTests
{
    private const int MaxFrameSize = 4 * 1024 * 1024;

    private static BifrostMessage WithPayload(int size)
    {
        var payload = new byte[size];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i * 7 + 3);
        return new BifrostMessage { RequestId = 9, Type = BifrostMessageType.Query, Query = "{ q }", Payload = payload };
    }

    [Fact]
    public async Task MessageWithinOneReceive_IsAViewOfTheReceiveBuffer()
    {
        var message = WithPayload(1000);
        var socket = new FakeWebSocket { Pipelined = true };
        socket.EnqueueMessage(message);
        var buffer = new byte[BifrostBinaryMiddleware.ReceiveBufferSize];

        var (data, type, owner) = await BifrostBinaryMiddleware.ReadFullMessageAsync(socket, buffer, CancellationToken.None);

        type.Should().Be(WebSocketMessageType.Binary);
        owner.Should().BeNull("a single receive needs no accumulation buffer");
        data.Array.Should().BeSameAs(buffer, "the frame is parsed in place, not copied");
        data.ToArray().Should().Equal(message.ToBytes());
    }

    [Fact]
    public async Task MessageLongerThanTheReceiveBuffer_IsAssembledWhole()
    {
        var message = WithPayload(3 * BifrostBinaryMiddleware.ReceiveBufferSize + 17);
        var socket = new FakeWebSocket { Pipelined = true };
        socket.EnqueueMessage(message);
        var buffer = new byte[BifrostBinaryMiddleware.ReceiveBufferSize];

        var (data, type, owner) = await BifrostBinaryMiddleware.ReadFullMessageAsync(socket, buffer, CancellationToken.None);
        using (owner)
        {
            type.Should().Be(WebSocketMessageType.Binary);
            owner.Should().NotBeNull();
            BifrostMessage.FromBytes(data.Array!, data.Offset, data.Count).Payload.Should().Equal(message.Payload);
        }
    }

    [Fact]
    public async Task MessageOverTheCap_IsRefused()
    {
        var socket = new FakeWebSocket { Pipelined = true };
        socket.EnqueueMessage(WithPayload(MaxFrameSize));
        var buffer = new byte[BifrostBinaryMiddleware.ReceiveBufferSize];

        var read = () => BifrostBinaryMiddleware.ReadFullMessageAsync(socket, buffer, CancellationToken.None);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*maximum size*");
    }
}
