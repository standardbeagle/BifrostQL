using FluentAssertions;
using Xunit;

namespace BifrostQL.Server;

/// <summary>
/// Tests for ChunkBuffer focusing on sequence-number edge cases,
/// especially near uint.MaxValue where a naive (int) cast overflows.
/// </summary>
public class ChunkBufferTests
{
    private static BifrostMessage MakeChunk(uint requestId, uint sequence, uint total) => new()
    {
        RequestId = requestId,
        Type = BifrostMessageType.Chunk,
        ChunkSequence = sequence,
        ChunkTotal = total,
        Payload = new byte[] { 0xCA, 0xFE },
    };

    // -----------------------------------------------------------------------
    // GetChunksAfter — overflow / boundary guards
    // -----------------------------------------------------------------------

    [Fact]
    public void GetChunksAfter_LastSequenceJustBelowIntMaxValue_ReturnsEmptyNotException()
    {
        // Arrange: a 3-chunk transfer; no chunk stored at sequence int.MaxValue + 1
        var buffer = new ChunkBuffer();
        const uint requestId = 1u;
        const uint total = 3u;
        buffer.Add(requestId, 0, MakeChunk(requestId, 0, total));
        buffer.Add(requestId, 1, MakeChunk(requestId, 1, total));
        buffer.Add(requestId, 2, MakeChunk(requestId, 2, total));

        // lastSequence near int.MaxValue — computed offset (int.MaxValue + 1) overflowed
        // to negative in the old code; new code must return empty without throwing.
        var lastSequence = (uint)int.MaxValue; // 2147483647

        // Act
        var act = () => buffer.GetChunksAfter(requestId, lastSequence);

        // Assert — no IndexOutOfRangeException or OverflowException
        act.Should().NotThrow();
        act().Should().BeEmpty("no chunks exist at or after that offset in a 3-chunk buffer");
    }

    [Fact]
    public void GetChunksAfter_LastSequenceUintMaxValueMinusOne_ReturnsEmptyNotException()
    {
        // Arrange
        var buffer = new ChunkBuffer();
        const uint requestId = 2u;
        const uint total = 5u;
        for (uint s = 0; s < total; s++)
            buffer.Add(requestId, s, MakeChunk(requestId, s, total));

        var lastSequence = uint.MaxValue - 1; // 4294967294 — next offset would overflow uint

        // Act & Assert
        var act = () => buffer.GetChunksAfter(requestId, lastSequence);
        act.Should().NotThrow();
        act().Should().BeEmpty("computed start index exceeds buffer length");
    }

    [Fact]
    public void GetChunksAfter_LastSequenceUintMaxValue_ReturnsSentinelAllChunks()
    {
        // uint.MaxValue is the protocol sentinel meaning "no chunks received, send all"
        var buffer = new ChunkBuffer();
        const uint requestId = 3u;
        const uint total = 3u;
        for (uint s = 0; s < total; s++)
            buffer.Add(requestId, s, MakeChunk(requestId, s, total));

        var chunks = buffer.GetChunksAfter(requestId, uint.MaxValue);

        chunks.Should().HaveCount(3, "uint.MaxValue sentinel means return all chunks");
    }

    [Fact]
    public void GetChunksAfter_NormalSequence_ReturnsCorrectTail()
    {
        var buffer = new ChunkBuffer();
        const uint requestId = 4u;
        const uint total = 5u;
        for (uint s = 0; s < total; s++)
            buffer.Add(requestId, s, MakeChunk(requestId, s, total));

        // Last received = 1, so we want chunks 2, 3, 4
        var chunks = buffer.GetChunksAfter(requestId, 1u);

        chunks.Should().HaveCount(3);
        chunks.Select(c => c.ChunkSequence).Should().Equal(2u, 3u, 4u);
    }

    // -----------------------------------------------------------------------
    // General ChunkBuffer correctness
    // -----------------------------------------------------------------------

    [Fact]
    public void Add_ThenTryGet_ReturnsStoredChunk()
    {
        var buffer = new ChunkBuffer();
        const uint requestId = 10u;
        var msg = MakeChunk(requestId, 0, 2);
        buffer.Add(requestId, 0, msg);

        var result = buffer.TryGet(requestId, 0);
        result.Should().NotBeNull();
        result!.ChunkSequence.Should().Be(0u);
    }

    [Fact]
    public void TryGet_UnknownRequest_ReturnsNull()
    {
        var buffer = new ChunkBuffer();
        buffer.TryGet(999u, 0u).Should().BeNull();
    }

    [Fact]
    public void Complete_RemovesEntry()
    {
        var buffer = new ChunkBuffer();
        const uint requestId = 20u;
        buffer.Add(requestId, 0, MakeChunk(requestId, 0, 1));
        buffer.Complete(requestId);

        buffer.Contains(requestId).Should().BeFalse();
        buffer.Count.Should().Be(0);
    }
}
