using System.IO.Hashing;
using BifrostQL.Server;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    public class ChunkMessageSerializationTests
    {
        [Fact]
        public void ChunkMessage_RoundTrips()
        {
            var payload = new byte[100];
            Random.Shared.NextBytes(payload);
            var msg = new BifrostMessage
            {
                RequestId = 10,
                Type = BifrostMessageType.Chunk,
                Payload = payload,
                ChunkSequence = 2,
                ChunkTotal = 5,
                ChunkOffset = 200,
                TotalBytes = 500,
                ChunkChecksum = 0xDEADBEEF,
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.RequestId.Should().Be(10);
            restored.Type.Should().Be(BifrostMessageType.Chunk);
            restored.Payload.Should().BeEquivalentTo(payload);
            restored.ChunkSequence.Should().Be(2);
            restored.ChunkTotal.Should().Be(5);
            restored.ChunkOffset.Should().Be(200);
            restored.TotalBytes.Should().Be(500);
            restored.ChunkChecksum.Should().Be(0xDEADBEEF);
        }

        [Fact]
        public void ChunkAckMessage_RoundTrips()
        {
            var msg = new BifrostMessage
            {
                RequestId = 42,
                Type = BifrostMessageType.ChunkAck,
                ChunkSequence = 7,
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.RequestId.Should().Be(42);
            restored.Type.Should().Be(BifrostMessageType.ChunkAck);
            restored.ChunkSequence.Should().Be(7);
        }

        [Fact]
        public void ChunkFields_DefaultToZero()
        {
            var msg = new BifrostMessage();
            msg.ChunkSequence.Should().Be(0);
            msg.ChunkTotal.Should().Be(0);
            msg.ChunkOffset.Should().Be(0);
            msg.TotalBytes.Should().Be(0);
            msg.ChunkChecksum.Should().Be(0);
        }

        [Fact]
        public void FirstChunk_WithZeroSequence_RoundTrips()
        {
            var msg = new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Chunk,
                Payload = new byte[] { 1, 2, 3 },
                ChunkSequence = 0,
                ChunkTotal = 3,
                ChunkOffset = 0,
                TotalBytes = 9,
                ChunkChecksum = Crc32.HashToUInt32(new byte[] { 1, 2, 3 }),
            };

            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.ChunkSequence.Should().Be(0);
            restored.ChunkTotal.Should().Be(3);
            restored.ChunkOffset.Should().Be(0);
            restored.TotalBytes.Should().Be(9);
        }

        [Theory]
        [InlineData(BifrostMessageType.Chunk, 4)]
        [InlineData(BifrostMessageType.ChunkAck, 5)]
        public void NewMessageTypes_HaveExpectedValues(BifrostMessageType type, int expected)
        {
            ((int)type).Should().Be(expected);
        }

        [Fact]
        public void LegacyMessage_WithoutChunkFields_StillDeserializes()
        {
            var msg = new BifrostMessage
            {
                RequestId = 99,
                Type = BifrostMessageType.Result,
                Payload = new byte[] { 10, 20, 30 },
            };
            var bytes = msg.ToBytes();
            var restored = BifrostMessage.FromBytes(bytes);

            restored.RequestId.Should().Be(99);
            restored.Type.Should().Be(BifrostMessageType.Result);
            restored.Payload.Should().BeEquivalentTo(new byte[] { 10, 20, 30 });
            restored.ChunkSequence.Should().Be(0);
            restored.ChunkTotal.Should().Be(0);
        }
    }

    public class ChunkSenderTests
    {
        [Theory]
        [InlineData(100, false)]
        [InlineData(64 * 1024, false)]
        [InlineData(64 * 1024 + 1, true)]
        [InlineData(200_000, true)]
        public void RequiresChunking_RespectsThreshold(int payloadSize, bool expected)
        {
            var sender = new ChunkSender();
            var msg = new BifrostMessage
            {
                Type = BifrostMessageType.Result,
                Payload = new byte[payloadSize],
            };

            sender.RequiresChunking(msg).Should().Be(expected);
        }

        [Fact]
        public void RequiresChunking_NonResultMessage_ReturnsFalse()
        {
            var sender = new ChunkSender();
            var msg = new BifrostMessage
            {
                Type = BifrostMessageType.Error,
                Payload = new byte[200_000],
            };

            sender.RequiresChunking(msg).Should().BeFalse();
        }

        [Fact]
        public void RequiresChunking_CustomThreshold()
        {
            var sender = new ChunkSender(chunkThreshold: 100);
            var small = new BifrostMessage { Type = BifrostMessageType.Result, Payload = new byte[100] };
            var large = new BifrostMessage { Type = BifrostMessageType.Result, Payload = new byte[101] };

            sender.RequiresChunking(small).Should().BeFalse();
            sender.RequiresChunking(large).Should().BeTrue();
        }

        [Fact]
        public void SplitIntoChunks_ProducesMultipleChunks()
        {
            var sender = new ChunkSender(chunkThreshold: 100);
            var payload = new byte[150];
            Random.Shared.NextBytes(payload);
            var msg = new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Result,
                Payload = payload,
            };

            var chunks = sender.SplitIntoChunks(msg);

            // Serialized message is larger than payload alone due to protobuf overhead
            chunks.Count.Should().BeGreaterThanOrEqualTo(2);

            // All chunks should have consistent metadata
            foreach (var chunk in chunks)
            {
                chunk.RequestId.Should().Be(1);
                chunk.Type.Should().Be(BifrostMessageType.Chunk);
                chunk.ChunkTotal.Should().Be((uint)chunks.Count);
                chunk.Payload.Should().NotBeEmpty();
            }

            // Sequence numbers should be contiguous starting from 0
            for (var i = 0; i < chunks.Count; i++)
                chunks[i].ChunkSequence.Should().Be((uint)i);
        }

        [Fact]
        public void SplitIntoChunks_ChunkOffsetsAreContinuous()
        {
            var sender = new ChunkSender(chunkThreshold: 100);
            var msg = new BifrostMessage
            {
                RequestId = 5,
                Type = BifrostMessageType.Result,
                Payload = new byte[300],
            };

            var chunks = sender.SplitIntoChunks(msg);

            // Verify offsets are contiguous
            ulong expectedOffset = 0;
            foreach (var chunk in chunks)
            {
                chunk.ChunkOffset.Should().Be(expectedOffset);
                expectedOffset += (ulong)chunk.Payload.Length;
            }

            // Total of all chunk payloads should equal TotalBytes
            expectedOffset.Should().Be(chunks[0].TotalBytes);
        }

        [Fact]
        public void SplitIntoChunks_EachChunkHasValidCrc32()
        {
            var sender = new ChunkSender(chunkThreshold: 50);
            var payload = new byte[150];
            Random.Shared.NextBytes(payload);

            var msg = new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Result,
                Payload = payload,
            };

            var chunks = sender.SplitIntoChunks(msg);

            foreach (var chunk in chunks)
            {
                var expectedCrc = Crc32.HashToUInt32(chunk.Payload);
                chunk.ChunkChecksum.Should().Be(expectedCrc);
            }
        }

        [Fact]
        public void ComputeCrc32_ProducesDeterministicResults()
        {
            var data = new byte[] { 1, 2, 3, 4, 5 };
            var crc1 = ChunkSender.ComputeCrc32(data);
            var crc2 = ChunkSender.ComputeCrc32(data);

            crc1.Should().Be(crc2);
            crc1.Should().NotBe(0);
        }

        [Fact]
        public void ComputeCrc32_DifferentDataProducesDifferentChecksums()
        {
            var data1 = new byte[] { 1, 2, 3 };
            var data2 = new byte[] { 4, 5, 6 };

            ChunkSender.ComputeCrc32(data1).Should().NotBe(ChunkSender.ComputeCrc32(data2));
        }

        [Fact]
        public void Constructor_RejectsInvalidThreshold()
        {
            var act = () => new ChunkSender(chunkThreshold: 0);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_RejectsInvalidAckWindow()
        {
            var act = () => new ChunkSender(ackWindow: 0);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    public class ChunkReceiverTests
    {
        [Fact]
        public void SingleChunkTransfer_ReturnsImmediately()
        {
            var receiver = new ChunkReceiver();
            var payload = new byte[] { 1, 2, 3, 4, 5 };

            var chunk = new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Chunk,
                Payload = payload,
                ChunkSequence = 0,
                ChunkTotal = 1,
                ChunkOffset = 0,
                TotalBytes = 5,
                ChunkChecksum = Crc32.HashToUInt32(payload),
            };

            var result = receiver.AddChunk(chunk);

            result.Should().NotBeNull();
            result.Should().BeEquivalentTo(payload);
        }

        [Fact]
        public void MultiChunkTransfer_ReturnsOnlyOnLastChunk()
        {
            var receiver = new ChunkReceiver();
            var fullPayload = new byte[150];
            for (var i = 0; i < fullPayload.Length; i++)
                fullPayload[i] = (byte)(i % 256);

            var chunk0Data = new byte[50];
            var chunk1Data = new byte[50];
            var chunk2Data = new byte[50];
            Buffer.BlockCopy(fullPayload, 0, chunk0Data, 0, 50);
            Buffer.BlockCopy(fullPayload, 50, chunk1Data, 0, 50);
            Buffer.BlockCopy(fullPayload, 100, chunk2Data, 0, 50);

            var result0 = receiver.AddChunk(MakeChunk(1, chunk0Data, 0, 3, 0, 150));
            result0.Should().BeNull();
            receiver.PendingCount.Should().Be(1);

            var result1 = receiver.AddChunk(MakeChunk(1, chunk1Data, 1, 3, 50, 150));
            result1.Should().BeNull();

            var result2 = receiver.AddChunk(MakeChunk(1, chunk2Data, 2, 3, 100, 150));
            result2.Should().NotBeNull();
            result2.Should().BeEquivalentTo(fullPayload);
            receiver.PendingCount.Should().Be(0);
        }

        [Fact]
        public void OutOfOrderChunks_StillReassembleCorrectly()
        {
            var receiver = new ChunkReceiver();
            var fullPayload = new byte[90];
            for (var i = 0; i < fullPayload.Length; i++)
                fullPayload[i] = (byte)(i + 10);

            var chunk0Data = new byte[30];
            var chunk1Data = new byte[30];
            var chunk2Data = new byte[30];
            Buffer.BlockCopy(fullPayload, 0, chunk0Data, 0, 30);
            Buffer.BlockCopy(fullPayload, 30, chunk1Data, 0, 30);
            Buffer.BlockCopy(fullPayload, 60, chunk2Data, 0, 30);

            // Send out of order: 2, 0, 1
            receiver.AddChunk(MakeChunk(1, chunk2Data, 2, 3, 60, 90)).Should().BeNull();
            receiver.AddChunk(MakeChunk(1, chunk0Data, 0, 3, 0, 90)).Should().BeNull();
            var result = receiver.AddChunk(MakeChunk(1, chunk1Data, 1, 3, 30, 90));

            result.Should().NotBeNull();
            result.Should().BeEquivalentTo(fullPayload);
        }

        [Fact]
        public void DuplicateChunk_IsIgnored()
        {
            var receiver = new ChunkReceiver();
            var chunk0Data = new byte[] { 1, 2, 3 };
            var chunk1Data = new byte[] { 4, 5, 6 };

            receiver.AddChunk(MakeChunk(1, chunk0Data, 0, 2, 0, 6)).Should().BeNull();
            // Send chunk 0 again (duplicate)
            receiver.AddChunk(MakeChunk(1, chunk0Data, 0, 2, 0, 6)).Should().BeNull();

            var result = receiver.AddChunk(MakeChunk(1, chunk1Data, 1, 2, 3, 6));
            result.Should().NotBeNull();
            result.Should().BeEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6 });
        }

        [Fact]
        public void InvalidChecksum_Throws()
        {
            var receiver = new ChunkReceiver();
            var chunk = new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Chunk,
                Payload = new byte[] { 1, 2, 3 },
                ChunkSequence = 0,
                ChunkTotal = 1,
                ChunkOffset = 0,
                TotalBytes = 3,
                ChunkChecksum = 0xBADBAD, // Wrong checksum
            };

            var act = () => receiver.AddChunk(chunk);
            act.Should().Throw<InvalidOperationException>().WithMessage("*CRC32 mismatch*");
        }

        [Fact]
        public void NonChunkMessage_Throws()
        {
            var receiver = new ChunkReceiver();
            var msg = new BifrostMessage { Type = BifrostMessageType.Query };

            var act = () => receiver.AddChunk(msg);
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void InvalidChunkSequence_Throws()
        {
            var receiver = new ChunkReceiver();
            var data = new byte[] { 1 };
            var chunk = new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Chunk,
                Payload = data,
                ChunkSequence = 5, // Out of range (total is 2)
                ChunkTotal = 2,
                ChunkOffset = 0,
                TotalBytes = 2,
                ChunkChecksum = Crc32.HashToUInt32(data),
            };

            var act = () => receiver.AddChunk(chunk);
            act.Should().Throw<InvalidOperationException>().WithMessage("*sequence*exceeds*");
        }

        [Fact]
        public void MultipleRequestIds_TrackedIndependently()
        {
            var receiver = new ChunkReceiver();
            var data1 = new byte[] { 10, 20 };
            var data2 = new byte[] { 30, 40 };

            // Request 1, chunk 0 of 2
            receiver.AddChunk(MakeChunk(1, new byte[] { 10 }, 0, 2, 0, 2)).Should().BeNull();
            // Request 2, chunk 0 of 2
            receiver.AddChunk(MakeChunk(2, new byte[] { 30 }, 0, 2, 0, 2)).Should().BeNull();

            receiver.PendingCount.Should().Be(2);

            // Complete request 2
            var result2 = receiver.AddChunk(MakeChunk(2, new byte[] { 40 }, 1, 2, 1, 2));
            result2.Should().NotBeNull();
            result2.Should().BeEquivalentTo(data2);
            receiver.PendingCount.Should().Be(1);

            // Complete request 1
            var result1 = receiver.AddChunk(MakeChunk(1, new byte[] { 20 }, 1, 2, 1, 2));
            result1.Should().NotBeNull();
            result1.Should().BeEquivalentTo(data1);
            receiver.PendingCount.Should().Be(0);
        }

        [Fact]
        public void CreateAck_ProducesCorrectMessage()
        {
            var ack = ChunkReceiver.CreateAck(42, 7);

            ack.RequestId.Should().Be(42);
            ack.Type.Should().Be(BifrostMessageType.ChunkAck);
            ack.ChunkSequence.Should().Be(7);
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

    public class ChunkSenderReceiverIntegrationTests
    {
        [Theory]
        [InlineData(150, 50)]
        [InlineData(200, 100)]
        [InlineData(1000, 128)]
        [InlineData(500, 500)]
        public void SplitAndReassemble_ProducesOriginalMessage(int payloadSize, int chunkThreshold)
        {
            var sender = new ChunkSender(chunkThreshold: chunkThreshold);
            var receiver = new ChunkReceiver();

            var originalPayload = new byte[payloadSize];
            Random.Shared.NextBytes(originalPayload);

            var response = new BifrostMessage
            {
                RequestId = 42,
                Type = BifrostMessageType.Result,
                Payload = originalPayload,
            };

            var chunks = sender.SplitIntoChunks(response);

            byte[]? assembledBytes = null;
            foreach (var chunk in chunks)
            {
                // Simulate wire transport: serialize and deserialize each chunk
                var wireBytes = chunk.ToBytes();
                var received = BifrostMessage.FromBytes(wireBytes);
                assembledBytes = receiver.AddChunk(received);
            }

            assembledBytes.Should().NotBeNull();
            var result = BifrostMessage.FromBytes(assembledBytes!);
            result.RequestId.Should().Be(42);
            result.Type.Should().Be(BifrostMessageType.Result);
            result.Payload.Should().BeEquivalentTo(originalPayload);
        }

        [Fact]
        public void SplitAndReassemble_OutOfOrder()
        {
            var sender = new ChunkSender(chunkThreshold: 50);
            var receiver = new ChunkReceiver();

            var originalPayload = new byte[200];
            Random.Shared.NextBytes(originalPayload);

            var response = new BifrostMessage
            {
                RequestId = 7,
                Type = BifrostMessageType.Result,
                Payload = originalPayload,
            };

            var chunks = sender.SplitIntoChunks(response);
            chunks.Count.Should().BeGreaterThanOrEqualTo(2);

            // Send in reverse order
            byte[]? assembledBytes = null;
            for (var i = chunks.Count - 1; i >= 0; i--)
            {
                var wireBytes = chunks[i].ToBytes();
                var received = BifrostMessage.FromBytes(wireBytes);
                assembledBytes = receiver.AddChunk(received);
                if (i > 0)
                    assembledBytes.Should().BeNull();
            }

            assembledBytes.Should().NotBeNull();
            var result = BifrostMessage.FromBytes(assembledBytes!);
            result.Payload.Should().BeEquivalentTo(originalPayload);
        }

        [Fact]
        public void LargePayload_ChunksCorrectly()
        {
            // Use default 64KB threshold with a 256KB payload
            var sender = new ChunkSender();
            var receiver = new ChunkReceiver();

            var originalPayload = new byte[256 * 1024];
            Random.Shared.NextBytes(originalPayload);

            var response = new BifrostMessage
            {
                RequestId = 100,
                Type = BifrostMessageType.Result,
                Payload = originalPayload,
            };

            sender.RequiresChunking(response).Should().BeTrue();
            var chunks = sender.SplitIntoChunks(response);
            // Serialized message is slightly larger than raw payload, so chunk count may be 5 not 4
            chunks.Count.Should().BeGreaterThanOrEqualTo(4);

            byte[]? assembledBytes = null;
            foreach (var chunk in chunks)
            {
                var wireBytes = chunk.ToBytes();
                var received = BifrostMessage.FromBytes(wireBytes);
                assembledBytes = receiver.AddChunk(received);
            }

            assembledBytes.Should().NotBeNull();
            var result = BifrostMessage.FromBytes(assembledBytes!);
            result.Payload.Should().BeEquivalentTo(originalPayload);
        }

        [Fact]
        public void CorruptedChunk_DetectedByReceiver()
        {
            var sender = new ChunkSender(chunkThreshold: 50);
            var receiver = new ChunkReceiver();

            var originalPayload = new byte[100];
            Random.Shared.NextBytes(originalPayload);

            var response = new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Result,
                Payload = originalPayload,
            };

            var chunks = sender.SplitIntoChunks(response);

            // Corrupt the payload of the second chunk
            var corruptedChunk = chunks[1];
            var wireBytes = corruptedChunk.ToBytes();
            var received = BifrostMessage.FromBytes(wireBytes);
            // Flip a bit in the payload
            received.Payload[0] ^= 0xFF;

            // First chunk succeeds
            receiver.AddChunk(BifrostMessage.FromBytes(chunks[0].ToBytes()));

            // Second chunk should fail CRC validation
            var act = () => receiver.AddChunk(received);
            act.Should().Throw<InvalidOperationException>().WithMessage("*CRC32 mismatch*");
        }

        [Fact]
        public void SplitAndReassemble_PreservesQueryMessage()
        {
            // Verify that a chunked Query message round-trips correctly
            var sender = new ChunkSender(chunkThreshold: 50);
            var receiver = new ChunkReceiver();

            var largeVariables = new string('x', 200);
            var queryMsg = new BifrostMessage
            {
                RequestId = 99,
                Type = BifrostMessageType.Query,
                Query = "{ users { id name } }",
                VariablesJson = "{\"filter\":\"" + largeVariables + "\"}",
            };

            // Force chunking by serializing and checking size
            var serialized = queryMsg.ToBytes();
            serialized.Length.Should().BeGreaterThan(50);

            var chunks = sender.SplitIntoChunks(queryMsg);
            chunks.Count.Should().BeGreaterThanOrEqualTo(2);

            byte[]? assembledBytes = null;
            foreach (var chunk in chunks)
            {
                var wireBytes = chunk.ToBytes();
                var received = BifrostMessage.FromBytes(wireBytes);
                assembledBytes = receiver.AddChunk(received);
            }

            assembledBytes.Should().NotBeNull();
            var result = BifrostMessage.FromBytes(assembledBytes!);
            result.RequestId.Should().Be(99);
            result.Type.Should().Be(BifrostMessageType.Query);
            result.Query.Should().Be("{ users { id name } }");
            result.VariablesJson.Should().Contain(largeVariables);
        }
    }
}
