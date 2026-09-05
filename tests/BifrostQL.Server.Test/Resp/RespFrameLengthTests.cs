using System.Text;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// The RESP decoder must bound the TOTAL byte length of one top-level frame, not just each
    /// element. The per-element caps left the frame itself unbounded: MaxBulkLength bounds ONE bulk
    /// string (1 MiB) and MaxAggregateElements bounds ONE aggregate's element count (1,048,576), so
    /// their product — about 1 TiB — is what a single frame from an UNAUTHENTICATED peer could
    /// reach, with every decoded element retained until the frame completes.
    ///
    /// <para>Each test drives the reader through a <see cref="CountingStream"/> so the assertions
    /// can distinguish "refused before the payload was consumed" from "buffered the payload and
    /// then complained": a cap that fires only after the bytes are materialized bounds nothing.</para>
    /// </summary>
    public sealed class RespFrameLengthTests
    {
        private const int BulkCap = 1 << 20;
        private const int ElementCap = 1 << 20;

        [Fact]
        public async Task A_bulk_string_over_the_frame_cap_is_refused_before_its_payload_is_read()
        {
            // Declared 64 KiB — comfortably under MaxBulkLength, so only a FRAME budget can refuse
            // it. The payload really is present on the wire: without the frame cap the reader
            // decodes it happily and returns a value.
            const int payloadLength = 64 * 1024;
            var wire = new MemoryStream();
            wire.Write(Encoding.ASCII.GetBytes($"${payloadLength}\r\n"));
            wire.Write(new byte[payloadLength]);
            wire.Write(Encoding.ASCII.GetBytes("\r\n"));
            wire.Position = 0;

            var counting = new CountingStream(wire);
            var reader = new RespReader(counting, BulkCap, ElementCap, 32, maxFrameLength: 4096);

            var act = async () => await reader.ReadValueAsync(default);

            (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("frame");
            // Before ALLOCATION: the 64 KiB payload was never pulled off the stream, so nothing
            // proportional to the declared length was materialized.
            // (The reader fills an 8 KiB buffer at a time, so one fill is the floor here; the point
            // is that nothing proportional to the DECLARED 64 KiB was ever pulled or allocated.)
            counting.BytesRead.Should().BeLessThanOrEqualTo(8192);
        }

        [Fact]
        public async Task An_aggregate_whose_elements_sum_past_the_frame_cap_is_refused_mid_frame()
        {
            // Every element is individually tiny and legal; only their SUM breaches the budget.
            // This is the shape the per-element caps cannot see at all.
            var wire = new MemoryStream();
            wire.Write(Encoding.ASCII.GetBytes("*100\r\n"));
            for (var i = 0; i < 100; i++)
            {
                wire.Write(Encoding.ASCII.GetBytes("$100\r\n"));
                wire.Write(new byte[100]);
                wire.Write(Encoding.ASCII.GetBytes("\r\n"));
            }
            var total = wire.Length;
            wire.Position = 0;

            var counting = new CountingStream(wire);
            var reader = new RespReader(counting, BulkCap, ElementCap, 32, maxFrameLength: 4096);

            var act = async () => await reader.ReadValueAsync(default);

            (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("frame");
            // Refused part-way: the whole ~10 KiB frame was never accumulated in memory.
            counting.BytesRead.Should().BeLessThan((int)total);
        }

        [Fact]
        public async Task The_budget_is_per_frame_and_resets_between_frames()
        {
            // A connection may send many frames, each within the cap. Resetting per TOP-LEVEL frame
            // (never inside one — an inner reset would defeat the guard) is what keeps a normal
            // session working while a single oversized frame is refused.
            var wire = new MemoryStream(Encoding.ASCII.GetBytes("$3\r\nfoo\r\n$3\r\nbar\r\n"));
            var reader = new RespReader(wire, BulkCap, ElementCap, 32, maxFrameLength: 16);

            (await reader.ReadValueAsync(default)).Should().BeOfType<RespBulkString>();
            (await reader.ReadValueAsync(default)).Should().BeOfType<RespBulkString>();
        }

        [Fact]
        public void The_listener_declares_a_concrete_frame_cap()
        {
            // Load-bearing: AGENTS.md's posture table records this number for the RESP row.
            new RespWireOptions().MaxFrameLength.Should().Be(1 << 20);
        }
    }
}
