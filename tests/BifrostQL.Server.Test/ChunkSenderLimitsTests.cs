using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Server;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Covers the two protocol hardening fixes on the chunked send path:
    /// PROTO-5 (per-sequence retransmit cap so a perpetually-NACKing client cannot pin the
    /// connection retransmitting forever) and PROTO-4 (a Query/Mutation frame that arrives
    /// mid-transfer is answered with an explicit Error rather than silently dropped).
    /// </summary>
    public class ChunkSenderLimitsTests
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
        public async Task PerpetualNack_ExceedsRetransmitCap_AbortsTransfer()
        {
            // Every ack-wait returns a NACK for sequence 0, so the window never drains.
            // With a cap of 3, the 4th NACK for the same sequence aborts the transfer.
            var sender = new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: new ChunkBuffer(),
                ackTimeout: TimeSpan.FromSeconds(30), logger: null, maxRetransmitsPerSequence: 3);
            var socket = new FakeWebSocket { DefaultIncomingType = BifrostMessageType.ChunkNack };
            var response = Result(20, 1000); // ~11 chunks, window of 2 forces ack waits

            var act = async () =>
                await sender.SendChunkedAsync(socket, response, AckBuffer(), CancellationToken.None);

            (await act.Should().ThrowAsync<ChunkRetransmitLimitExceededException>())
                .Which.Sequence.Should().Be(0u);
        }

        [Fact]
        public async Task NacksUnderCap_StillComplete()
        {
            // Exactly cap (3) NACKs for sequence 0, then acks drain the rest: the transfer
            // must complete without tripping the cap.
            var sender = new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: new ChunkBuffer(),
                ackTimeout: TimeSpan.FromSeconds(30), logger: null, maxRetransmitsPerSequence: 3);
            var socket = new FakeWebSocket();
            socket.EnqueueNack(requestId: 21, sequence: 0);
            socket.EnqueueNack(requestId: 21, sequence: 0);
            socket.EnqueueNack(requestId: 21, sequence: 0);
            var response = Result(21, 1000);

            await sender.SendChunkedAsync(socket, response, AckBuffer(), CancellationToken.None);

            var seq0Count = socket.SentMessages().Count(m =>
                m.Type == BifrostMessageType.Chunk && m.ChunkSequence == 0);
            seq0Count.Should().Be(4, "chunk 0 is sent once initially plus three NACK retransmissions");
        }

        [Fact]
        public void Constructor_RejectsNonPositiveRetransmitCap()
        {
            var act = () => new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: null,
                maxRetransmitsPerSequence: 0);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public async Task PipelinedQueryMidTransfer_IsAnsweredWithError_NotSilentlyDropped()
        {
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 2);
            var socket = new FakeWebSocket();
            // First ack-wait (window full) receives a pipelined Query instead of an ack.
            // Subsequent (default) acks let the transfer finish.
            socket.EnqueueMessage(new BifrostMessage
            {
                RequestId = 99,
                Type = BifrostMessageType.Query,
                Query = "{ concurrent }",
            });
            var response = Result(3, 600);

            await sender.SendChunkedAsync(socket, response, AckBuffer(), CancellationToken.None);

            var sent = socket.SentMessages();
            sent.Should().Contain(
                m => m.Type == BifrostMessageType.Error && m.RequestId == 99,
                "a request pipelined during a chunked transfer must get an explicit Error");
            sent.Single(m => m.Type == BifrostMessageType.Error && m.RequestId == 99)
                .Errors.Should().ContainSingle()
                .Which.Should().Contain("one request at a time");
        }
    }
}
