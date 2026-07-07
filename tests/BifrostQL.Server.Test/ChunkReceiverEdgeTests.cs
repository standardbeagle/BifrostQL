using System.IO.Hashing;
using BifrostQL.Server;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Adversarial and boundary coverage for ChunkReceiver reassembly: oversized
    /// transfers, hostile offsets, degenerate chunk counts, empty fragments, and
    /// empty transfers.
    /// </summary>
    public class ChunkReceiverEdgeTests
    {
        private static BifrostMessage Chunk(
            uint requestId, byte[] data, uint sequence, uint total, ulong offset, ulong totalBytes,
            uint? checksumOverride = null)
        {
            return new BifrostMessage
            {
                RequestId = requestId,
                Type = BifrostMessageType.Chunk,
                Payload = data,
                ChunkSequence = sequence,
                ChunkTotal = total,
                ChunkOffset = offset,
                TotalBytes = totalBytes,
                ChunkChecksum = checksumOverride ?? Crc32.HashToUInt32(data),
            };
        }

        [Fact]
        public void TotalBytesExceedingIntMax_Throws()
        {
            var receiver = new ChunkReceiver();
            var data = new byte[] { 1, 2, 3 };
            // Checksum valid; the oversize guard must fire before any allocation.
            var chunk = Chunk(1, data, 0, 1, 0, (ulong)int.MaxValue + 1);

            var act = () => receiver.AddChunk(chunk);

            act.Should().Throw<InvalidOperationException>().WithMessage("*too large*");
        }

        [Fact]
        public void OutOfBoundsOffset_Throws()
        {
            var receiver = new ChunkReceiver();
            var data = new byte[] { 1, 2, 3, 4, 5 };
            // CRC matches but the offset pushes the copy past the declared buffer length.
            var chunk = Chunk(1, data, 0, 1, offset: 4, totalBytes: 5);

            var act = () => receiver.AddChunk(chunk);

            act.Should().Throw<InvalidOperationException>().WithMessage("*exceeds payload size*");
        }

        [Fact]
        public void OffsetNearUlongMax_Throws_WithoutOverflow()
        {
            // A hostile offset close to ulong.MaxValue must be rejected by the guard.
            // If the guard summed offset+length it would wrap to a small value, slip
            // past the check, and let (int)offset truncate negative — surfacing a raw
            // ArgumentException from Buffer.BlockCopy instead of a clear domain error.
            var receiver = new ChunkReceiver();
            var data = new byte[] { 1, 2, 3 };
            var chunk = Chunk(1, data, 0, 1, offset: ulong.MaxValue - 2, totalBytes: 5);

            var act = () => receiver.AddChunk(chunk);

            act.Should().Throw<InvalidOperationException>().WithMessage("*exceeds payload size*");
        }

        [Fact]
        public void ChunkTotalZero_Throws()
        {
            var receiver = new ChunkReceiver();
            var data = new byte[] { 1 };
            var chunk = Chunk(1, data, 0, total: 0, offset: 0, totalBytes: 1);

            var act = () => receiver.AddChunk(chunk);

            act.Should().Throw<InvalidOperationException>().WithMessage("*exceeds*");
        }

        [Fact]
        public void ZeroLengthMiddleChunk_ReassemblesCorrectly()
        {
            var receiver = new ChunkReceiver();
            // Three chunks where the middle fragment is empty (CRC32 of empty data is 0).
            receiver.AddChunk(Chunk(1, new byte[] { 1, 2, 3 }, 0, 3, 0, 6)).Should().BeNull();
            receiver.AddChunk(Chunk(1, Array.Empty<byte>(), 1, 3, 3, 6)).Should().BeNull();
            var result = receiver.AddChunk(Chunk(1, new byte[] { 4, 5, 6 }, 2, 3, 3, 6));

            result.Should().Equal(1, 2, 3, 4, 5, 6);
        }

        [Fact]
        public void EmptyTransfer_ReturnsEmptyPayload()
        {
            var receiver = new ChunkReceiver();
            var chunk = Chunk(1, Array.Empty<byte>(), 0, 1, 0, 0);

            var result = receiver.AddChunk(chunk);

            result.Should().NotBeNull();
            result.Should().BeEmpty();
            receiver.PendingCount.Should().Be(0);
        }

        [Fact]
        public void TotalBytesExceedingConfiguredCap_ThrowsBeforeAllocation()
        {
            var receiver = new ChunkReceiver();
            var data = new byte[] { 1, 2, 3 };
            // Just over the 64 MB default cap: must be rejected up front, not allocated.
            var chunk = Chunk(1, data, 0, 1, 0, (ulong)ChunkReceiver.DefaultMaxReassemblyBytes + 1);

            var act = () => receiver.AddChunk(chunk);

            act.Should().Throw<InvalidOperationException>().WithMessage("*too large*");
            receiver.PendingCount.Should().Be(0);
        }

        [Fact]
        public void HugeChunkTotal_ThrowsInsteadOfAllocatingTrackingArray()
        {
            var receiver = new ChunkReceiver();
            var data = new byte[] { 1 };
            // A hostile ChunkTotal near uint.MaxValue would previously allocate a ~4 GB
            // bool[] from a single frame; it must be rejected before any allocation.
            var chunk = Chunk(1, data, 0, total: uint.MaxValue, offset: 0, totalBytes: 1);

            var act = () => receiver.AddChunk(chunk);

            act.Should().Throw<InvalidOperationException>().WithMessage("*chunk*limit*");
            receiver.PendingCount.Should().Be(0);
        }

        [Fact]
        public void ChunkTotalJustAboveCap_Throws_AtCapSucceeds()
        {
            var receiver = new ChunkReceiver();
            var overCap = Chunk(1, new byte[] { 1 }, 0, total: (uint)ChunkReceiver.MaxChunkCount + 1, offset: 0, totalBytes: 1);
            var act = () => receiver.AddChunk(overCap);
            act.Should().Throw<InvalidOperationException>();

            var atCap = Chunk(2, new byte[] { 1 }, 0, total: ChunkReceiver.MaxChunkCount, offset: 0, totalBytes: 1);
            receiver.AddChunk(atCap).Should().BeNull("only 1 of 65536 chunks arrived");
            receiver.PendingCount.Should().Be(1);
        }

        [Fact]
        public void PendingReassemblyLimit_RejectsNewTransfersWhenFull()
        {
            var receiver = new ChunkReceiver(
                maxReassemblyBytes: 1024, maxPendingReassemblies: 2, reassemblyTtl: TimeSpan.FromMinutes(5));

            // Two incomplete transfers occupy the pending table.
            receiver.AddChunk(Chunk(1, new byte[] { 1 }, 0, 2, 0, 2)).Should().BeNull();
            receiver.AddChunk(Chunk(2, new byte[] { 1 }, 0, 2, 0, 2)).Should().BeNull();

            var act = () => receiver.AddChunk(Chunk(3, new byte[] { 1 }, 0, 2, 0, 2));

            act.Should().Throw<InvalidOperationException>().WithMessage("*pending*");
            // Existing transfers keep working: completing transfer 1 still succeeds.
            receiver.AddChunk(Chunk(1, new byte[] { 2 }, 1, 2, 1, 2)).Should().Equal(1, 2);
        }

        [Fact]
        public void AggregateInFlightBytes_OverConfiguredTotal_IsRejected()
        {
            // Finding 8: many concurrent sessions must not sum past the aggregate cap even
            // when each individual session is under the per-session limit. Per-session cap
            // 1000, up to 8 sessions, but aggregate capped at 1500 bytes total.
            var receiver = new ChunkReceiver(
                maxReassemblyBytes: 1000,
                maxPendingReassemblies: 8,
                reassemblyTtl: TimeSpan.FromMinutes(5),
                maxTotalReassemblyBytes: 1500);

            // Two sessions each declaring 800 bytes: first is fine (800 <= 1500), the second
            // would push the aggregate to 1600 > 1500 and must be rejected before allocation.
            receiver.AddChunk(Chunk(1, new byte[] { 1 }, 0, 2, 0, 800)).Should().BeNull();
            var act = () => receiver.AddChunk(Chunk(2, new byte[] { 1 }, 0, 2, 0, 800));

            act.Should().Throw<InvalidOperationException>().WithMessage("*Aggregate*");
        }

        [Fact]
        public void AggregateInFlightBytes_FreedOnCompletion_AllowsNewTransfers()
        {
            var receiver = new ChunkReceiver(
                maxReassemblyBytes: 1000,
                maxPendingReassemblies: 8,
                reassemblyTtl: TimeSpan.FromMinutes(5),
                maxTotalReassemblyBytes: 1500);

            // Complete a 800-byte transfer (frees its aggregate reservation)...
            receiver.AddChunk(Chunk(1, new byte[] { 1, 2, 3, 4 }, 0, 2, 0, 8)).Should().BeNull();
            receiver.AddChunk(Chunk(1, new byte[] { 5, 6, 7, 8 }, 1, 2, 4, 8)).Should().NotBeNull();

            // ...then two large sessions both fit because the first was released.
            receiver.AddChunk(Chunk(2, new byte[] { 1 }, 0, 2, 0, 800)).Should().BeNull();
            receiver.AddChunk(Chunk(3, new byte[] { 1 }, 0, 2, 0, 700)).Should().BeNull();
        }

        [Fact]
        public void StalePendingReassembly_IsEvicted_MakingRoomForNewTransfers()
        {
            var receiver = new ChunkReceiver(
                maxReassemblyBytes: 1024, maxPendingReassemblies: 1, reassemblyTtl: TimeSpan.FromMilliseconds(50));

            receiver.AddChunk(Chunk(1, new byte[] { 1 }, 0, 2, 0, 2)).Should().BeNull();
            receiver.PendingCount.Should().Be(1);

            Thread.Sleep(100); // let the abandoned transfer go stale

            // A new transfer evicts the stale one instead of being rejected.
            receiver.AddChunk(Chunk(2, new byte[] { 1 }, 0, 2, 0, 2)).Should().BeNull();
            receiver.PendingCount.Should().Be(1);
        }

        [Fact]
        public void ReceiverConstructor_RejectsNonPositiveLimits()
        {
            ((Action)(() => new ChunkReceiver(0, 1, TimeSpan.FromSeconds(1))))
                .Should().Throw<ArgumentOutOfRangeException>();
            ((Action)(() => new ChunkReceiver(1024, 0, TimeSpan.FromSeconds(1))))
                .Should().Throw<ArgumentOutOfRangeException>();
            ((Action)(() => new ChunkReceiver(1024, 1, TimeSpan.Zero)))
                .Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void AssembledBufferLength_MatchesDeclaredTotalBytes()
        {
            var receiver = new ChunkReceiver();
            receiver.AddChunk(Chunk(1, new byte[] { 1, 2 }, 0, 2, 0, 5));
            var result = receiver.AddChunk(Chunk(1, new byte[] { 3, 4, 5 }, 1, 2, 2, 5));

            result.Should().NotBeNull();
            result!.Length.Should().Be(5);
        }
    }
}
