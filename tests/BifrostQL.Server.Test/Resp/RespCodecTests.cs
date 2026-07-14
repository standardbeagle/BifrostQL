using BifrostQL.Server.Resp;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// Codec round-trip + framing-robustness tests for the RESP2/RESP3 reader and writer.
    /// Round-trip is proven at the byte level: encode → decode → re-encode must reproduce the
    /// original bytes, which is stronger than value equality (and side-steps NaN self-inequality
    /// for doubles). Malformed and oversized frames must raise a clean
    /// <see cref="RespProtocolException"/>, never an unhandled throw.
    /// </summary>
    public sealed class RespCodecTests
    {
        // ---- RESP2 types ----

        [Fact]
        public async Task SimpleString_RoundTrips()
            => await AssertRoundTrips(RespValue.Simple("OK"));

        [Fact]
        public async Task Error_RoundTrips()
            => await AssertRoundTrips(RespValue.Err("WRONGPASS invalid username-password pair or user is disabled."));

        [Theory]
        [InlineData(0)]
        [InlineData(12345)]
        [InlineData(-7)]
        [InlineData(long.MaxValue)]
        [InlineData(long.MinValue)]
        public async Task Integer_RoundTrips(long value)
            => await AssertRoundTrips(RespValue.Int(value));

        [Fact]
        public async Task BulkString_RoundTrips()
            => await AssertRoundTrips(RespValue.Bulk("hello world"));

        [Fact]
        public async Task EmptyBulkString_RoundTrips()
            => await AssertRoundTrips(RespValue.Bulk(string.Empty));

        [Fact]
        public async Task NullBulkString_RoundTrips()
        {
            // Arrange / Act
            var decoded = await RoundTripAsync(RespValue.NullBulk);

            // Assert: the $-1 null bulk decodes back to a null-valued bulk string.
            decoded.Should().BeOfType<RespBulkString>().Which.Value.Should().BeNull();
        }

        [Fact]
        public async Task NullArray_RoundTrips()
        {
            // Arrange / Act
            var decoded = await RoundTripAsync(RespValue.NullArray);

            // Assert: the *-1 null array decodes back to a null-item array.
            decoded.Should().BeOfType<RespArray>().Which.Items.Should().BeNull();
        }

        [Fact]
        public async Task NestedArray_WithNullElement_RoundTrips()
        {
            // Arrange: a nested array mixing RESP2 scalars and a RESP3 null element.
            var value = RespValue.Arr(
                RespValue.Int(1),
                RespValue.Bulk("a"),
                RespValue.Arr(RespValue.Bulk("x"), RespNull.Instance));

            // Act / Assert
            await AssertRoundTrips(value);
        }

        // ---- RESP3 additions ----

        [Fact]
        public async Task Null_RoundTrips()
            => await AssertRoundTrips(RespNull.Instance);

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Boolean_RoundTrips(bool value)
            => await AssertRoundTrips(new RespBoolean(value));

        [Theory]
        [InlineData(3.14)]
        [InlineData(-0.5)]
        [InlineData(0.0)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(double.NaN)]
        public async Task Double_RoundTrips(double value)
            => await AssertRoundTrips(new RespDouble(value));

        [Fact]
        public async Task BigNumber_RoundTrips()
            => await AssertRoundTrips(new RespBigNumber("3492890328409238509324850943850943825024385"));

        [Fact]
        public async Task VerbatimString_RoundTrips()
            => await AssertRoundTrips(new RespVerbatimString("txt", "Some string\nwith a newline"));

        [Fact]
        public async Task Map_RoundTrips()
        {
            // Arrange: a HELLO-shaped map of mixed value types.
            var value = new RespMap(new[]
            {
                new KeyValuePair<RespValue, RespValue>(RespValue.Bulk("server"), RespValue.Bulk("bifrostql")),
                new KeyValuePair<RespValue, RespValue>(RespValue.Bulk("proto"), RespValue.Int(3)),
            });

            // Act / Assert
            await AssertRoundTrips(value);
        }

        [Fact]
        public async Task Set_RoundTrips()
            => await AssertRoundTrips(new RespSet(new RespValue[] { RespValue.Int(1), RespValue.Int(2), RespValue.Int(3) }));

        [Fact]
        public async Task Push_RoundTrips()
            => await AssertRoundTrips(new RespPush(new RespValue[]
            {
                RespValue.Bulk("message"), RespValue.Bulk("channel"), RespValue.Bulk("payload"),
            }));

        // ---- framing robustness ----

        [Theory]
        [InlineData("?garbage\r\n")]        // unknown type marker
        [InlineData("$abc\r\n")]            // non-numeric bulk length
        [InlineData("*xy\r\n")]             // non-numeric array length
        [InlineData("$5\r\nhi\r\n")]        // bulk shorter than its declared length (truncated at EOF)
        [InlineData("$2\r\nhiZZ")]          // bulk missing its CRLF terminator
        [InlineData(":notanumber\r\n")]     // non-numeric integer
        public async Task MalformedFrame_RaisesCleanProtocolException(string wire)
        {
            // Arrange
            var reader = new RespReader(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(wire)), 1 << 20, 1 << 20, 32);

            // Act
            var act = async () => await reader.ReadValueAsync(default);

            // Assert: a clean protocol exception, not an unhandled BCL/IO throw.
            await act.Should().ThrowAsync<RespProtocolException>();
        }

        [Fact]
        public async Task OversizedBulkLength_IsRejected_NotAllocated()
        {
            // Arrange: a bulk length far beyond the cap must be refused before allocation (DoS guard).
            var reader = new RespReader(new MemoryStream(System.Text.Encoding.ASCII.GetBytes("$1000\r\n")), maxBulkLength: 16, maxElements: 16, maxNestingDepth: 32);

            // Act
            var act = async () => await reader.ReadValueAsync(default);

            // Assert
            await act.Should().ThrowAsync<RespProtocolException>();
        }

        [Fact]
        public async Task OversizedAggregateLength_IsRejected_NotAllocated()
        {
            // Arrange: a huge multibulk element count must be refused before pre-allocating (DoS guard).
            var reader = new RespReader(new MemoryStream(System.Text.Encoding.ASCII.GetBytes("*1000000\r\n")), maxBulkLength: 16, maxElements: 8, maxNestingDepth: 32);

            // Act
            var act = async () => await reader.ReadValueAsync(default);

            // Assert
            await act.Should().ThrowAsync<RespProtocolException>();
        }

        [Fact]
        public async Task DeeplyNestedAggregate_ExceedingDepthCap_IsRejected_NoStackOverflow()
        {
            // Arrange: cap+1 nested single-element array headers. With the whole frame buffered
            // the reads complete synchronously, so an unguarded recursive decoder would grow the
            // physical stack one frame per level → uncatchable StackOverflowException tearing down
            // the host. The depth guard must turn this into a clean, catchable protocol error.
            const int cap = 8;
            var wire = string.Concat(System.Linq.Enumerable.Repeat("*1\r\n", cap + 1)) + ":1\r\n";
            var reader = new RespReader(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(wire)), 1 << 20, 1 << 20, maxNestingDepth: cap);

            // Act
            var act = async () => await reader.ReadValueAsync(default);

            // Assert: rejected as a protocol error (the caught base), never a crash.
            await act.Should().ThrowAsync<RespProtocolException>();
        }

        [Fact]
        public async Task Nesting_UpToDepthCap_StillParses()
        {
            // Arrange: exactly `cap` nested single-element arrays around an integer leaf — the
            // deepest input the guard must still accept (depth cap is headroom, not a false trip).
            const int cap = 8;
            var wire = string.Concat(System.Linq.Enumerable.Repeat("*1\r\n", cap)) + ":42\r\n";
            var reader = new RespReader(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(wire)), 1 << 20, 1 << 20, maxNestingDepth: cap);

            // Act
            var decoded = await reader.ReadValueAsync(default);

            // Assert: it parses to the nested-array structure with the integer at the bottom.
            var node = decoded;
            for (var i = 0; i < cap; i++)
                node = node.Should().BeOfType<RespArray>().Which.Items!.Should().ContainSingle().Which;
            node.Should().BeOfType<RespInteger>().Which.Value.Should().Be(42);
        }

        [Fact]
        public async Task LyingAggregateLength_WithTinyPrefix_RejectsOnTruncation_WithoutProportionalAllocation()
        {
            // Arrange: a ~14-byte prefix declares a million elements but supplies none. The reader
            // must NOT pre-allocate a million-slot array up front; it grows incrementally and hits
            // end-of-stream after the header, throwing a clean protocol error instead.
            var reader = new RespReader(new MemoryStream(System.Text.Encoding.ASCII.GetBytes("*1000000\r\n")), 1 << 20, maxElements: 1 << 20, maxNestingDepth: 32);

            // Act
            var act = async () => await reader.ReadValueAsync(default);

            // Assert: rejected on truncation, having only materialized the elements that arrived (none).
            await act.Should().ThrowAsync<RespProtocolException>()
                .WithMessage("*end of stream*");
        }

        // ---- helpers ----

        private static async Task AssertRoundTrips(RespValue value)
        {
            var original = RespWriter.EncodeToArray(value);
            var decoded = await RoundTripAsync(value);

            // Byte-level round-trip: re-encoding the decoded value reproduces the original wire bytes.
            RespWriter.EncodeToArray(decoded).Should().Equal(original);
        }

        private static async Task<RespValue> RoundTripAsync(RespValue value)
        {
            var bytes = RespWriter.EncodeToArray(value);
            var reader = new RespReader(new MemoryStream(bytes), 1 << 20, 1 << 20, 32);
            var decoded = await reader.ReadValueAsync(default);
            decoded.Should().NotBeNull("the encoded value must decode back to a frame");
            return decoded!;
        }
    }
}
