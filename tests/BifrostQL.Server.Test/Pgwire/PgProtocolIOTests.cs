using System.Buffers.Binary;
using System.Text;
using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>Unit tests for the pgwire length-prefixed framing over a byte stream.</summary>
    public sealed class PgProtocolIOTests
    {
        [Fact]
        public async Task ReadStartupPacket_DecodesCodeAndPayload()
        {
            // Arrange: an SSLRequest packet — [Int32 len=8][Int32 code].
            var packet = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), 8);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), PgWireProtocol.SslRequestCode);
            using var stream = new MemoryStream(packet);

            // Act
            var (code, payload) = await PgProtocolIO.ReadStartupPacketAsync(stream, default);

            // Assert
            code.Should().Be(PgWireProtocol.SslRequestCode);
            payload.Should().BeEmpty();
        }

        [Fact]
        public async Task ReadStartupPacket_OversizeLength_Throws()
        {
            // Arrange: a length prefix beyond the hard cap must be refused, not allocated.
            var packet = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(packet, PgProtocolIO.MaxMessageLength + 1);
            using var stream = new MemoryStream(packet);

            // Act + Assert
            var act = async () => await PgProtocolIO.ReadStartupPacketAsync(stream, default);
            await act.Should().ThrowAsync<PgProtocolException>();
        }

        [Fact]
        public async Task WriteThenRead_TypedMessage_RoundTrips()
        {
            // Arrange
            using var stream = new MemoryStream();
            var body = Encoding.UTF8.GetBytes("hello");

            // Act: write a typed message, rewind, read it back.
            await PgProtocolIO.WriteMessageAsync(stream, (byte)'p', body, default);
            stream.Position = 0;
            var message = await PgProtocolIO.ReadMessageAsync(stream, default);

            // Assert
            message.Type.Should().Be((byte)'p');
            message.Body.Should().Equal(body);
        }

        [Fact]
        public void ParseStartupParameters_ReadsKeyValuePairs()
        {
            // Arrange: user\0alice\0database\0app\0\0
            using var ms = new MemoryStream();
            foreach (var s in new[] { "user", "alice", "database", "app" })
            {
                ms.Write(Encoding.UTF8.GetBytes(s));
                ms.WriteByte(0);
            }
            ms.WriteByte(0); // final terminator

            // Act
            var parameters = PgProtocolIO.ParseStartupParameters(ms.ToArray());

            // Assert
            parameters["user"].Should().Be("alice");
            parameters["database"].Should().Be("app");
        }
    }
}
