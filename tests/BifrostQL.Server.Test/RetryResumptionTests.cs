using System.IO.Hashing;
using BifrostQL.Server;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    public class ResumeMessageSerializationTests
    {
        [Fact]
        public void ResumeMessage_RoundTrips()
        {
            var msg = new BifrostMessage
            {
                RequestId = 42,
                Type = BifrostMessageType.Resume,
                LastSequence = 5,
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.RequestId.Should().Be(42);
            restored.Type.Should().Be(BifrostMessageType.Resume);
            restored.LastSequence.Should().Be(5);
        }

        [Fact]
        public void ResumeAckMessage_RoundTrips()
        {
            var msg = new BifrostMessage
            {
                RequestId = 42,
                Type = BifrostMessageType.ResumeAck,
                ChunkTotal = 3,
                LastSequence = 5,
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.RequestId.Should().Be(42);
            restored.Type.Should().Be(BifrostMessageType.ResumeAck);
            restored.ChunkTotal.Should().Be(3);
            restored.LastSequence.Should().Be(5);
        }

        [Fact]
        public void ChunkNackMessage_RoundTrips()
        {
            var msg = new BifrostMessage
            {
                RequestId = 10,
                Type = BifrostMessageType.ChunkNack,
                ChunkSequence = 3,
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.RequestId.Should().Be(10);
            restored.Type.Should().Be(BifrostMessageType.ChunkNack);
            restored.ChunkSequence.Should().Be(3);
        }

        [Theory]
        [InlineData(BifrostMessageType.Resume, 6)]
        [InlineData(BifrostMessageType.ResumeAck, 7)]
        [InlineData(BifrostMessageType.ChunkNack, 8)]
        public void NewMessageTypes_HaveExpectedValues(BifrostMessageType type, int expected)
        {
            ((int)type).Should().Be(expected);
        }

        [Fact]
        public void LastSequence_DefaultsToZero()
        {
            var msg = new BifrostMessage();
            msg.LastSequence.Should().Be(0);
        }

        [Fact]
        public void LastSequence_MaxValue_RoundTrips()
        {
            var msg = new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Resume,
                LastSequence = uint.MaxValue,
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.LastSequence.Should().Be(uint.MaxValue);
        }

        [Fact]
        public void LegacyMessage_WithoutLastSequence_StillDeserializes()
        {
            var msg = new BifrostMessage
            {
                RequestId = 99,
                Type = BifrostMessageType.Result,
                Payload = new byte[] { 10, 20, 30 },
            };
            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.LastSequence.Should().Be(0);
        }
    }

    public class ChunkBufferTests
    {
        [Fact]
        public void Add_And_TryGet_ReturnsStoredChunk()
        {
            var buffer = new ChunkBuffer();
            var chunk = MakeChunk(1, new byte[] { 1, 2, 3 }, 0, 3, 0, 9);

            buffer.Add(1, 0, chunk);

            var retrieved = buffer.TryGet(1, 0);
            retrieved.Should().NotBeNull();
            retrieved!.RequestId.Should().Be(1);
            retrieved.ChunkSequence.Should().Be(0);
        }

        [Fact]
        public void TryGet_NonExistent_ReturnsNull()
        {
            var buffer = new ChunkBuffer();

            buffer.TryGet(999, 0).Should().BeNull();
        }

        [Fact]
        public void TryGet_WrongSequence_ReturnsNull()
        {
            var buffer = new ChunkBuffer();
            var chunk = MakeChunk(1, new byte[] { 1, 2, 3 }, 0, 3, 0, 9);
            buffer.Add(1, 0, chunk);

            buffer.TryGet(1, 5).Should().BeNull();
        }

        [Fact]
        public void Contains_ReturnsTrueForExistingEntry()
        {
            var buffer = new ChunkBuffer();
            var chunk = MakeChunk(1, new byte[] { 1, 2, 3 }, 0, 1, 0, 3);
            buffer.Add(1, 0, chunk);

            buffer.Contains(1).Should().BeTrue();
            buffer.Contains(2).Should().BeFalse();
        }

        [Fact]
        public void Complete_RemovesEntry()
        {
            var buffer = new ChunkBuffer();
            var chunk = MakeChunk(1, new byte[] { 1, 2, 3 }, 0, 1, 0, 3);
            buffer.Add(1, 0, chunk);

            buffer.Complete(1);

            buffer.Contains(1).Should().BeFalse();
            buffer.TryGet(1, 0).Should().BeNull();
            buffer.Count.Should().Be(0);
        }

        [Fact]
        public void Count_ReflectsActiveEntries()
        {
            var buffer = new ChunkBuffer();
            buffer.Count.Should().Be(0);

            buffer.Add(1, 0, MakeChunk(1, new byte[] { 1 }, 0, 2, 0, 2));
            buffer.Count.Should().Be(1);

            buffer.Add(2, 0, MakeChunk(2, new byte[] { 2 }, 0, 1, 0, 1));
            buffer.Count.Should().Be(2);

            buffer.Complete(1);
            buffer.Count.Should().Be(1);
        }

        [Fact]
        public void GetChunksAfter_ReturnsRemainingChunks()
        {
            var buffer = new ChunkBuffer();
            buffer.Add(1, 0, MakeChunk(1, new byte[] { 10 }, 0, 4, 0, 4));
            buffer.Add(1, 1, MakeChunk(1, new byte[] { 20 }, 1, 4, 1, 4));
            buffer.Add(1, 2, MakeChunk(1, new byte[] { 30 }, 2, 4, 2, 4));
            buffer.Add(1, 3, MakeChunk(1, new byte[] { 40 }, 3, 4, 3, 4));

            var remaining = buffer.GetChunksAfter(1, 1);

            remaining.Should().HaveCount(2);
            remaining[0].ChunkSequence.Should().Be(2);
            remaining[1].ChunkSequence.Should().Be(3);
        }

        [Fact]
        public void GetChunksAfter_MaxValue_ReturnsAllChunks()
        {
            var buffer = new ChunkBuffer();
            buffer.Add(1, 0, MakeChunk(1, new byte[] { 10 }, 0, 3, 0, 3));
            buffer.Add(1, 1, MakeChunk(1, new byte[] { 20 }, 1, 3, 1, 3));
            buffer.Add(1, 2, MakeChunk(1, new byte[] { 30 }, 2, 3, 2, 3));

            var remaining = buffer.GetChunksAfter(1, uint.MaxValue);

            remaining.Should().HaveCount(3);
            remaining[0].ChunkSequence.Should().Be(0);
            remaining[1].ChunkSequence.Should().Be(1);
            remaining[2].ChunkSequence.Should().Be(2);
        }

        [Fact]
        public void GetChunksAfter_LastChunk_ReturnsEmpty()
        {
            var buffer = new ChunkBuffer();
            buffer.Add(1, 0, MakeChunk(1, new byte[] { 10 }, 0, 2, 0, 2));
            buffer.Add(1, 1, MakeChunk(1, new byte[] { 20 }, 1, 2, 1, 2));

            var remaining = buffer.GetChunksAfter(1, 1);

            remaining.Should().BeEmpty();
        }

        [Fact]
        public void GetChunksAfter_NonExistentRequest_ReturnsEmpty()
        {
            var buffer = new ChunkBuffer();

            var remaining = buffer.GetChunksAfter(999, 0);

            remaining.Should().BeEmpty();
        }

        [Fact]
        public void ExpiredEntry_IsEvicted()
        {
            var shortTtl = TimeSpan.FromMilliseconds(50);
            var buffer = new ChunkBuffer(shortTtl);
            buffer.Add(1, 0, MakeChunk(1, new byte[] { 1 }, 0, 1, 0, 1));

            buffer.Contains(1).Should().BeTrue();

            // Wait for TTL to expire
            Thread.Sleep(100);

            buffer.TryGet(1, 0).Should().BeNull();
            buffer.Contains(1).Should().BeFalse();
        }

        [Fact]
        public void ExpiredEntry_GetChunksAfter_ReturnsEmpty()
        {
            var shortTtl = TimeSpan.FromMilliseconds(50);
            var buffer = new ChunkBuffer(shortTtl);
            buffer.Add(1, 0, MakeChunk(1, new byte[] { 1 }, 0, 1, 0, 1));

            Thread.Sleep(100);

            buffer.GetChunksAfter(1, uint.MaxValue).Should().BeEmpty();
        }

        [Fact]
        public void Constructor_RejectsZeroTtl()
        {
            var act = () => new ChunkBuffer(TimeSpan.Zero);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_RejectsNegativeTtl()
        {
            var act = () => new ChunkBuffer(TimeSpan.FromSeconds(-1));
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void MultipleRequests_TrackedIndependently()
        {
            var buffer = new ChunkBuffer();
            buffer.Add(1, 0, MakeChunk(1, new byte[] { 10 }, 0, 2, 0, 2));
            buffer.Add(1, 1, MakeChunk(1, new byte[] { 20 }, 1, 2, 1, 2));
            buffer.Add(2, 0, MakeChunk(2, new byte[] { 30 }, 0, 1, 0, 1));

            buffer.Count.Should().Be(2);

            buffer.Complete(1);
            buffer.Count.Should().Be(1);
            buffer.Contains(2).Should().BeTrue();

            var r2Chunks = buffer.GetChunksAfter(2, uint.MaxValue);
            r2Chunks.Should().HaveCount(1);
            r2Chunks[0].Payload.Should().BeEquivalentTo(new byte[] { 30 });
        }

        [Fact]
        public void DuplicateAdd_OverwritesChunk()
        {
            var buffer = new ChunkBuffer();
            var chunk1 = MakeChunk(1, new byte[] { 10 }, 0, 1, 0, 1);
            var chunk2 = MakeChunk(1, new byte[] { 20 }, 0, 1, 0, 1);

            buffer.Add(1, 0, chunk1);
            buffer.Add(1, 0, chunk2);

            var retrieved = buffer.TryGet(1, 0);
            retrieved.Should().NotBeNull();
            retrieved!.Payload.Should().BeEquivalentTo(new byte[] { 20 });
        }

        private static BifrostMessage MakeChunk(
            uint requestId, byte[] data, uint sequence, uint total, ulong offset, ulong totalBytes)
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
                ChunkChecksum = Crc32.HashToUInt32(data),
            };
        }
    }

    public class ChunkSenderBufferIntegrationTests
    {
        [Fact]
        public void SplitIntoChunks_WithBuffer_StoresAllChunks()
        {
            var buffer = new ChunkBuffer();
            var sender = new ChunkSender(chunkThreshold: 50, ackWindow: 8, chunkBuffer: buffer);

            var payload = new byte[150];
            Random.Shared.NextBytes(payload);
            var msg = new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Result,
                Payload = payload,
            };

            var chunks = sender.SplitIntoChunks(msg);

            // Manually store chunks in buffer (simulating what SendChunksAsync does)
            foreach (var chunk in chunks)
                buffer.Add(chunk.RequestId, chunk.ChunkSequence, chunk);

            buffer.Contains(1).Should().BeTrue();

            for (uint i = 0; i < (uint)chunks.Count; i++)
            {
                var retrieved = buffer.TryGet(1, i);
                retrieved.Should().NotBeNull();
                retrieved!.ChunkSequence.Should().Be(i);
                retrieved.Payload.Should().BeEquivalentTo(chunks[(int)i].Payload);
            }
        }

        [Fact]
        public void SplitIntoChunks_WithBuffer_ResumeReturnsRemainingChunks()
        {
            var buffer = new ChunkBuffer();
            var sender = new ChunkSender(chunkThreshold: 50, ackWindow: 8, chunkBuffer: buffer);

            var payload = new byte[200];
            Random.Shared.NextBytes(payload);
            var msg = new BifrostMessage
            {
                RequestId = 5,
                Type = BifrostMessageType.Result,
                Payload = payload,
            };

            var chunks = sender.SplitIntoChunks(msg);
            chunks.Count.Should().BeGreaterThanOrEqualTo(3);

            // Store all chunks in buffer
            foreach (var chunk in chunks)
                buffer.Add(chunk.RequestId, chunk.ChunkSequence, chunk);

            // Simulate client received first 2 chunks, then disconnected
            var remaining = buffer.GetChunksAfter(5, 1);
            remaining.Count.Should().Be(chunks.Count - 2);
            remaining[0].ChunkSequence.Should().Be(2);
        }

        [Fact]
        public void SplitAndResume_ProducesOriginalMessage()
        {
            var buffer = new ChunkBuffer();
            var sender = new ChunkSender(chunkThreshold: 50, ackWindow: 8, chunkBuffer: buffer);
            var receiver = new ChunkReceiver();

            var originalPayload = new byte[200];
            Random.Shared.NextBytes(originalPayload);
            var msg = new BifrostMessage
            {
                RequestId = 7,
                Type = BifrostMessageType.Result,
                Payload = originalPayload,
            };

            var chunks = sender.SplitIntoChunks(msg);
            foreach (var chunk in chunks)
                buffer.Add(chunk.RequestId, chunk.ChunkSequence, chunk);

            // Simulate: client received first 2 chunks only
            for (var i = 0; i < 2 && i < chunks.Count; i++)
            {
                var wireBytes = chunks[i].ToBytes();
                var received = BifrostMessage.FromBytes(wireBytes);
                receiver.AddChunk(received);
            }

            // Simulate: client reconnects with Resume, server retransmits remaining
            var remaining = buffer.GetChunksAfter(7, 1);

            byte[]? assembledBytes = null;
            foreach (var retransmitted in remaining)
            {
                var wireBytes = retransmitted.ToBytes();
                var received = BifrostMessage.FromBytes(wireBytes);
                assembledBytes = receiver.AddChunk(received);
            }

            assembledBytes.Should().NotBeNull();
            var result = BifrostMessage.FromBytes(assembledBytes!);
            result.RequestId.Should().Be(7);
            result.Type.Should().Be(BifrostMessageType.Result);
            result.Payload.Should().BeEquivalentTo(originalPayload);
        }

        [Fact]
        public void ChunkNack_RetransmitsSpecificChunk()
        {
            var buffer = new ChunkBuffer();
            var sender = new ChunkSender(chunkThreshold: 50, ackWindow: 8, chunkBuffer: buffer);

            var payload = new byte[150];
            Random.Shared.NextBytes(payload);
            var msg = new BifrostMessage
            {
                RequestId = 10,
                Type = BifrostMessageType.Result,
                Payload = payload,
            };

            var chunks = sender.SplitIntoChunks(msg);
            foreach (var chunk in chunks)
                buffer.Add(chunk.RequestId, chunk.ChunkSequence, chunk);

            // Simulate NACK for chunk 1
            var retransmitted = buffer.TryGet(10, 1);
            retransmitted.Should().NotBeNull();
            retransmitted!.ChunkSequence.Should().Be(1);

            // Verify the retransmitted chunk has correct CRC32
            var expectedCrc = Crc32.HashToUInt32(retransmitted.Payload);
            retransmitted.ChunkChecksum.Should().Be(expectedCrc);
        }

        [Fact]
        public void IdempotentDelivery_DuplicateChunksIgnoredByReceiver()
        {
            var buffer = new ChunkBuffer();
            var receiver = new ChunkReceiver();

            var data0 = new byte[] { 1, 2, 3 };
            var data1 = new byte[] { 4, 5, 6 };

            var chunk0 = MakeChunk(1, data0, 0, 2, 0, 6);
            var chunk1 = MakeChunk(1, data1, 1, 2, 3, 6);

            buffer.Add(1, 0, chunk0);
            buffer.Add(1, 1, chunk1);

            // Deliver chunk 0
            receiver.AddChunk(chunk0).Should().BeNull();
            // Deliver chunk 0 again (duplicate - idempotent)
            receiver.AddChunk(chunk0).Should().BeNull();
            // Deliver chunk 1 - completes
            var result = receiver.AddChunk(chunk1);

            result.Should().NotBeNull();
            result.Should().BeEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6 });
        }

        private static BifrostMessage MakeChunk(
            uint requestId, byte[] data, uint sequence, uint total, ulong offset, ulong totalBytes)
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
                ChunkChecksum = Crc32.HashToUInt32(data),
            };
        }
    }

    public class ChunkBufferConcurrencyTests
    {
        [Fact]
        public async Task ConcurrentAddAndRead_DoesNotThrow()
        {
            var buffer = new ChunkBuffer();
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            var tasks = new List<Task>();
            for (uint reqId = 0; reqId < 10; reqId++)
            {
                var capturedReqId = reqId;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        for (uint seq = 0; seq < 5; seq++)
                        {
                            var data = new byte[] { (byte)capturedReqId, (byte)seq };
                            var chunk = new BifrostMessage
                            {
                                RequestId = capturedReqId,
                                Type = BifrostMessageType.Chunk,
                                Payload = data,
                                ChunkSequence = seq,
                                ChunkTotal = 5,
                                ChunkOffset = seq * 2,
                                TotalBytes = 10,
                                ChunkChecksum = Crc32.HashToUInt32(data),
                            };
                            buffer.Add(capturedReqId, seq, chunk);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }));
            }

            // Readers running concurrently
            for (uint reqId = 0; reqId < 10; reqId++)
            {
                var capturedReqId = reqId;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        for (var i = 0; i < 20; i++)
                        {
                            buffer.TryGet(capturedReqId, (uint)(i % 5));
                            buffer.GetChunksAfter(capturedReqId, (uint)(i % 5));
                            buffer.Contains(capturedReqId);
                            await Task.Delay(1);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }));
            }

            await Task.WhenAll(tasks);
            exceptions.Should().BeEmpty();
        }

        [Fact]
        public async Task ConcurrentComplete_DoesNotThrow()
        {
            var buffer = new ChunkBuffer();

            for (uint reqId = 0; reqId < 10; reqId++)
            {
                var data = new byte[] { (byte)reqId };
                var chunk = new BifrostMessage
                {
                    RequestId = reqId,
                    Type = BifrostMessageType.Chunk,
                    Payload = data,
                    ChunkSequence = 0,
                    ChunkTotal = 1,
                    ChunkOffset = 0,
                    TotalBytes = 1,
                    ChunkChecksum = Crc32.HashToUInt32(data),
                };
                buffer.Add(reqId, 0, chunk);
            }

            var tasks = new List<Task>();
            for (uint reqId = 0; reqId < 10; reqId++)
            {
                var capturedReqId = reqId;
                tasks.Add(Task.Run(() => buffer.Complete(capturedReqId)));
            }

            await Task.WhenAll(tasks);
            buffer.Count.Should().Be(0);
        }
    }
}
