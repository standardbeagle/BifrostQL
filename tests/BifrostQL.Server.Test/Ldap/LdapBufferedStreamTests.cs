using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// The read-buffering wrapper the LDAP connection loop frames through. Its security-relevant
    /// property is <see cref="LdapBufferedStream.BufferedByteCount"/>: bytes a peer pipelined behind
    /// the message currently being decoded must be VISIBLE to this process, because that is what the
    /// StartTLS handler checks before it agrees to negotiate a transport upgrade.
    /// </summary>
    public sealed class LdapBufferedStreamTests
    {
        [Fact]
        public async Task PipelinedBytes_RemainVisible_AfterTheFirstMessageIsConsumed()
        {
            // Arrange: one burst carrying four bytes; the "framing reader" consumes only the first two.
            var source = new MemoryStream(new byte[] { 1, 2, 3, 4 });
            var buffered = new LdapBufferedStream(source);

            // Act: read a byte at a time, as the framing reader does.
            var one = new byte[1];
            (await buffered.ReadAsync(one)).Should().Be(1);
            (await buffered.ReadAsync(one)).Should().Be(1);

            // Assert: the trailing two bytes are held here, not lost in the transport.
            buffered.BufferedByteCount.Should().Be(2);
        }

        [Fact]
        public async Task BufferedByteCount_IsZero_WhenTheBurstIsFullyConsumed()
        {
            var buffered = new LdapBufferedStream(new MemoryStream(new byte[] { 7, 8 }));

            var two = new byte[2];
            (await buffered.ReadAsync(two)).Should().Be(2);

            buffered.BufferedByteCount.Should().Be(0, "nothing was pipelined behind the consumed bytes");
        }

        [Fact]
        public async Task Reads_DeliverEveryByte_InOrder_AcrossBufferRefills()
        {
            var payload = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
            var buffered = new LdapBufferedStream(new MemoryStream(payload), bufferSize: 64);

            var read = new List<byte>();
            var one = new byte[1];
            while (await buffered.ReadAsync(one) == 1)
                read.Add(one[0]);

            read.Should().Equal(payload);
            buffered.BufferedByteCount.Should().Be(0);
        }

        [Fact]
        public async Task Writes_PassStraightThrough_SoNoResponseIsStrandedInABuffer()
        {
            var sink = new MemoryStream();
            var buffered = new LdapBufferedStream(sink);

            await buffered.WriteAsync(new byte[] { 0x30, 0x00 });

            sink.ToArray().Should().Equal(0x30, 0x00);
        }
    }
}
