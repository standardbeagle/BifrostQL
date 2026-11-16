using BifrostQL.Server;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    public class PooledBufferStreamTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(16 * 1024)]          // exactly the first rental
        [InlineData(16 * 1024 + 1)]      // one byte past it: the first growth
        [InlineData(3_000_000)]          // several growths, large-object-heap sized
        public async Task ToArray_ReturnsExactlyTheBytesWritten_AcrossGrowth(int size)
        {
            var expected = Enumerable.Range(0, size).Select(i => (byte)(i * 31 + 7)).ToArray();
            using var stream = new PooledBufferStream();

            // Mixed write shapes, in pieces that do not line up with the rental sizes.
            var written = 0;
            var step = 0;
            while (written < size)
            {
                var count = Math.Min(size - written, 1 + (step * 7919) % 50_000);
                switch (step++ % 4)
                {
                    case 0: stream.Write(expected, written, count); break;
                    case 1: stream.Write(expected.AsSpan(written, count)); break;
                    case 2: await stream.WriteAsync(expected.AsMemory(written, count)); break;
                    default:
                        stream.WriteByte(expected[written]);
                        count = 1;
                        break;
                }
                written += count;
            }

            stream.Length.Should().Be(size);
            stream.ToArray().Should().Equal(expected);
        }

        [Fact]
        public void IsWriteOnly_AndRefusesUseAfterDispose()
        {
            var stream = new PooledBufferStream();
            stream.CanRead.Should().BeFalse();
            stream.CanSeek.Should().BeFalse();

            stream.Dispose();
            stream.Dispose();

            stream.CanWrite.Should().BeFalse();
            FluentActions.Invoking(() => stream.ToArray()).Should().Throw<ObjectDisposedException>();
            FluentActions.Invoking(() => stream.Write(new byte[] { 1 })).Should().Throw<ObjectDisposedException>();
        }
    }
}
