using System.Net.WebSockets;
using BifrostQL.Server;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Exercises the async WebSocket send path: ChunkSender.SendChunkedAsync /
    /// SendChunksAsync — backpressure window, ACK draining, NACK retransmission,
    /// buffer population, and premature-close handling. Driven by a scriptable
    /// FakeWebSocket so no real connection is needed.
    /// </summary>
    public class ChunkSenderSendTests
    {
        private static byte[] AckBuffer() => new byte[64 * 1024];

        private static BifrostMessage Result(uint requestId, int payloadSize)
        {
            var payload = new byte[payloadSize];
            for (var i = 0; i < payload.Length; i++)
                payload[i] = (byte)((i * 31 + 7) & 0xFF);
            return new BifrostMessage
            {
                RequestId = requestId,
                Type = BifrostMessageType.Result,
                Payload = payload,
            };
        }

        [Fact]
        public async Task SendChunkedAsync_SmallResult_SendsSingleFrameWithoutWaiting()
        {
            var sender = new ChunkSender(); // default 64KB threshold
            var socket = new FakeWebSocket();
            var response = Result(1, 100);

            await sender.SendChunkedAsync(socket, response, AckBuffer(), CancellationToken.None);

            socket.SentFrames.Should().HaveCount(1);
            // The sender now drains the full outstanding window before returning (so a late
            // NACK on the final chunk can still be served before the buffer is released), so
            // the single chunk's ACK is awaited: exactly one receive, no more.
            socket.ReceiveCount.Should().Be(1, "the final chunk's ack is drained before completion");
            socket.SentMessages()[0].Type.Should().Be(BifrostMessageType.Chunk);
        }

        [Fact]
        public async Task SendChunkedAsync_LargePayload_AllChunksSentAndReassembleToOriginal()
        {
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 8);
            var socket = new FakeWebSocket(); // default frames are ChunkAcks
            var response = Result(42, 1000);

            await sender.SendChunkedAsync(socket, response, AckBuffer(), CancellationToken.None);

            var sent = socket.SentMessages();
            sent.Should().HaveCountGreaterThanOrEqualTo(10);
            sent.Should().OnlyContain(m => m.Type == BifrostMessageType.Chunk);

            // Sequences are contiguous from 0
            for (var i = 0; i < sent.Count; i++)
                sent[i].ChunkSequence.Should().Be((uint)i);

            // Reassemble the wire frames and recover the original Result
            var receiver = new ChunkReceiver();
            byte[]? assembled = null;
            foreach (var frame in socket.SentFrames)
                assembled = receiver.AddChunk(BifrostMessage.FromBytes(frame));

            assembled.Should().NotBeNull();
            var restored = BifrostMessage.FromBytes(assembled!);
            restored.RequestId.Should().Be(42);
            restored.Type.Should().Be(BifrostMessageType.Result);
            restored.Payload.Should().BeEquivalentTo(response.Payload);
        }

        [Fact]
        public async Task SendChunksAsync_WindowFull_BlocksUntilAck()
        {
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 2);
            var socket = new FakeWebSocket();
            var response = Result(5, 1000); // ~11 chunks, window of 2 forces waits

            await sender.SendChunkedAsync(socket, response, AckBuffer(), CancellationToken.None);

            socket.ReceiveCount.Should().BeGreaterThan(0, "the window of 2 must pause and wait for acks");
            socket.SentMessages().Should().OnlyContain(m => m.Type == BifrostMessageType.Chunk);
        }

        [Fact]
        public async Task SendChunksAsync_WithBuffer_StoresEveryChunkForResumption()
        {
            var buffer = new ChunkBuffer();
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 8, chunkBuffer: buffer);
            var socket = new FakeWebSocket();
            var response = Result(7, 500);

            await sender.SendChunkedAsync(socket, response, AckBuffer(), CancellationToken.None);

            var sent = socket.SentMessages();
            buffer.Contains(7).Should().BeTrue();
            for (uint seq = 0; seq < (uint)sent.Count; seq++)
                buffer.TryGet(7, seq).Should().NotBeNull();
        }

        [Fact]
        public async Task SendChunksAsync_CloseFrameMidStream_Throws()
        {
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 2);
            var socket = new FakeWebSocket();
            socket.EnqueueClose(); // first ack-wait gets a Close instead of an ack
            var response = Result(9, 1000);

            var act = async () =>
                await sender.SendChunkedAsync(socket, response, AckBuffer(), CancellationToken.None);

            await act.Should().ThrowAsync<WebSocketException>()
                .Where(e => e.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely);
        }

        [Fact]
        public async Task SendChunksAsync_Nack_RetransmitsRequestedChunkFromBuffer()
        {
            var buffer = new ChunkBuffer();
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 2, chunkBuffer: buffer);
            var socket = new FakeWebSocket();
            // First ack-wait (after chunk 1 sent) returns a NACK for sequence 0,
            // then defaults to acks so the rest of the transfer drains.
            socket.EnqueueNack(requestId: 11, sequence: 0);
            var response = Result(11, 1000);

            await sender.SendChunkedAsync(socket, response, AckBuffer(), CancellationToken.None);

            var seq0Count = socket.SentMessages().Count(m => m.ChunkSequence == 0);
            seq0Count.Should().Be(2, "chunk 0 is sent once initially and once on NACK retransmission");
        }

        [Fact]
        public async Task SendChunksAsync_NackOnFinalChunk_IsHonoredDuringTailDrain()
        {
            // Finding 4: the tail (last ackWindow-1 chunks) must still be retransmittable when
            // the client NACKs it. With a window wider than the chunk count, all chunks are
            // sent without a mid-transfer wait; the sender then DRAINS the outstanding acks,
            // and a NACK on the final sequence during that drain is served from the buffer —
            // instead of the old behavior where the buffer was released before the drain.
            var buffer = new ChunkBuffer();
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 64, chunkBuffer: buffer);
            var socket = new FakeWebSocket();
            var response = Result(21, 500); // 5 chunks: sequences 0..4
            var finalSeq = 4u;
            // The client's first frame during the tail drain NACKs the final chunk.
            socket.EnqueueNack(requestId: 21, sequence: finalSeq);

            var leftover = await sender.SendChunkedAsync(socket, response, AckBuffer(), CancellationToken.None);

            leftover.Should().BeNull("a NACK is handled internally, not returned as a leftover");
            var finalSeqSends = socket.SentMessages().Count(m => m.ChunkSequence == finalSeq && m.Type == BifrostMessageType.Chunk);
            finalSeqSends.Should().Be(2, "the final chunk is sent once initially and retransmitted on the tail NACK");
        }

        [Fact]
        public async Task SendChunksAsync_PipelinedFrameDuringTailDrain_IsReturnedAsLeftover()
        {
            // A non-ack frame the client pipelines after the transfer (e.g. its next Query)
            // stops the tail drain and is returned so the caller processes it as the next
            // request — preserving serial semantics instead of consuming/dropping it.
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 64);
            var socket = new FakeWebSocket();
            var response = Result(31, 500);
            var pipelined = new BifrostMessage { RequestId = 32, Type = BifrostMessageType.Query, Query = "{ x }" };
            socket.EnqueueMessage(pipelined);

            var leftover = await sender.SendChunkedAsync(socket, response, AckBuffer(), CancellationToken.None);

            leftover.Should().NotBeNull();
            leftover!.RequestId.Should().Be(32);
            leftover.Type.Should().Be(BifrostMessageType.Query);
        }

        [Fact]
        public async Task SendChunkedAsync_ClientNeverAcks_ThrowsTimeoutInsteadOfBlockingForever()
        {
            var sender = new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: null,
                ackTimeout: TimeSpan.FromMilliseconds(100));
            var socket = new FakeWebSocket { HangWhenDrained = true };
            var response = Result(13, 1000); // ~11 chunks, window of 2 forces an ack wait

            var act = async () =>
                await sender.SendChunkedAsync(socket, response, AckBuffer(), CancellationToken.None);

            await act.Should().ThrowAsync<TimeoutException>();
        }

        [Fact]
        public async Task SendChunkedAsync_ExternalCancellation_ThrowsCancellation_NotTimeout()
        {
            var sender = new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: null,
                ackTimeout: TimeSpan.FromSeconds(30));
            var socket = new FakeWebSocket { HangWhenDrained = true };
            var response = Result(14, 1000);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            var act = async () =>
                await sender.SendChunkedAsync(socket, response, AckBuffer(), cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public void Constructor_RejectsNonPositiveAckTimeout()
        {
            var act = () => new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: null, ackTimeout: TimeSpan.Zero);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public async Task SendChunksAsync_NonBinaryAckFrame_TreatedAsNoAck()
        {
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 2);
            var socket = new FakeWebSocket();
            // A stray text frame yields zero acks; the sender keeps waiting and the
            // following (default) ack lets it progress. Transfer must still complete.
            socket.EnqueueText();
            var response = Result(3, 600);

            await sender.SendChunkedAsync(socket, response, AckBuffer(), CancellationToken.None);

            var receiver = new ChunkReceiver();
            byte[]? assembled = null;
            foreach (var frame in socket.SentFrames)
                assembled = receiver.AddChunk(BifrostMessage.FromBytes(frame));
            assembled.Should().NotBeNull();
            BifrostMessage.FromBytes(assembled!).Payload.Should().BeEquivalentTo(response.Payload);
        }
    }
}
