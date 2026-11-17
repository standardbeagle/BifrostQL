using System.Text;
using BifrostQL.Server.Ldap;
using BifrostQL.Server.Pgwire;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Size caps count received bytes, never declared ones. Every raw-wire front door used to
    /// check a frame's declared length against its cap and then allocate the whole declared length
    /// before any body byte arrived, so a pre-auth peer could pin ~1 MiB per socket by sending a
    /// length prefix and stalling. Each fact declares a body just under the cap, sends 16 bytes of
    /// it and ends the stream, and measures what the reader allocated on this thread (the
    /// MemoryStream completes every read synchronously, so the whole read runs on it).
    /// </summary>
    public sealed class ReceivedByteBufferTests
    {
        private const int Declared = 1_000_000;
        private const long AllocationBudget = 128 * 1024;

        [Fact]
        public async Task Pgwire_message_body_is_not_allocated_at_its_declared_length()
        {
            var frame = new byte[5 + 16];
            frame[0] = (byte)'Q';
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), Declared);

            var allocated = await AllocatedWhileFailing<EndOfStreamException>(
                () => PgProtocolIO.ReadMessageAsync(new MemoryStream(frame), CancellationToken.None));

            allocated.Should().BeLessThan(AllocationBudget);
        }

        [Fact]
        public async Task Pgwire_startup_packet_is_not_allocated_at_its_declared_length()
        {
            var frame = new byte[4 + 16];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame, Declared);

            var allocated = await AllocatedWhileFailing<EndOfStreamException>(
                () => PgProtocolIO.ReadStartupPacketAsync(new MemoryStream(frame), CancellationToken.None));

            allocated.Should().BeLessThan(AllocationBudget);
        }

        [Fact]
        public async Task Ldap_message_body_is_not_allocated_at_its_declared_length()
        {
            // SEQUENCE, long-form length (3 bytes) = Declared, then 16 body bytes and EOF.
            var frame = new byte[5 + 16];
            frame[0] = 0x30;
            frame[1] = 0x83;
            frame[2] = unchecked((byte)(Declared >> 16));
            frame[3] = unchecked((byte)(Declared >> 8));
            frame[4] = unchecked((byte)Declared);
            var reader = new LdapMessageReader(
                maxMessageLength: 1024 * 1024, maxNestingDepth: 32, maxFilterComponents: 1024, maxSearchAttributes: 1024);

            var allocated = await AllocatedWhileFailing<LdapProtocolException>(
                () => reader.ReadRequestAsync(new MemoryStream(frame), CancellationToken.None));

            allocated.Should().BeLessThan(AllocationBudget);
        }

        [Fact]
        public async Task Resp_bulk_string_is_not_allocated_at_its_declared_length()
        {
            var frame = Encoding.ASCII.GetBytes($"*1\r\n${Declared}\r\n" + new string('x', 16));
            var reader = new RespReader(
                new MemoryStream(frame), maxBulkLength: 1024 * 1024, maxElements: 1024,
                maxNestingDepth: 8, maxFrameLength: 2 * 1024 * 1024);

            var allocated = await AllocatedWhileFailing<RespProtocolException>(
                () => reader.ReadValueAsync(CancellationToken.None));

            allocated.Should().BeLessThan(AllocationBudget);
        }

        [Fact]
        public async Task A_body_delivered_in_pieces_is_returned_whole_and_exactly_sized()
        {
            var payload = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
            var stream = new TrickleStream(payload, chunk: 1000);

            var body = await ReceivedByteBuffer.ReadAsync(stream, payload.Length, CancellationToken.None);

            body.Should().Equal(payload);
        }

        [Fact]
        public void Capacity_grows_with_received_bytes_and_never_past_the_declared_length()
        {
            var body = new ReceivedByteBuffer(40_000);
            body.Capacity.Should().Be(ReceivedByteBuffer.InitialCapacity);

            body.Advance(body.FreeSpace().Length);
            body.FreeSpace().Length.Should().Be(ReceivedByteBuffer.InitialCapacity);
            body.Capacity.Should().Be(2 * ReceivedByteBuffer.InitialCapacity);

            body.Advance(body.FreeSpace().Length);
            body.FreeSpace().Length.Should().Be(40_000 - 2 * ReceivedByteBuffer.InitialCapacity);
            body.Capacity.Should().Be(40_000);
        }

        private static async Task<long> AllocatedWhileFailing<TException>(Func<Task> read)
            where TException : Exception
        {
            await Task.Yield();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var thrown = await Record.ExceptionAsync(read);
            var after = GC.GetAllocatedBytesForCurrentThread();
            thrown.Should().BeAssignableTo<TException>("a frame shorter than its declared length is a protocol error");
            return after - before;
        }

        private sealed class TrickleStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _chunk;
            private int _position;

            public TrickleStream(byte[] data, int chunk) { _data = data; _chunk = chunk; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var n = Math.Min(Math.Min(count, _chunk), _data.Length - _position);
                Array.Copy(_data, _position, buffer, offset, n);
                _position += n;
                return n;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
