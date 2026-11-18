using System.Threading.Channels;
using BifrostQL.Server;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Exercises <see cref="ChunkTransfer"/>: the ack window, the tail drain, NACK
    /// retransmission, buffer population, the ack timeout and the per-sequence retransmit cap.
    /// A scripted client task reads the frames the transfer sends and answers through
    /// <see cref="ChunkTransfer.Acknowledge"/>, in order, the way the connection's reader does.
    /// </summary>
    public class ChunkTransferTests
    {
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

        /// <summary>
        /// A client that acks every chunk it receives, retransmits included. <see cref="ExtraBefore"/>
        /// injects frames (NACKs) ahead of the ack of the Nth received chunk; <see cref="Silent"/>
        /// never answers; <see cref="NackOnly"/> answers every chunk with a NACK of sequence 0.
        /// </summary>
        private sealed class ScriptedClient
        {
            private readonly Channel<BifrostMessage> _received = Channel.CreateUnbounded<BifrostMessage>();
            public List<BifrostMessage> Sent { get; } = new();
            public Dictionary<int, List<BifrostMessage>> ExtraBefore { get; } = new();
            public bool Silent { get; init; }
            public bool NackOnly { get; init; }
            /// <summary>Received-chunk indices answered with a NACK of that chunk instead of an ack.</summary>
            public HashSet<int> NackInsteadOfAck { get; } = new();
            public int MaxInFlightSeen { get; private set; }
            /// <summary>Runs before each reply: a consumer that takes its time over every chunk.</summary>
            public Func<Task>? BeforeEachReply { get; init; }
            private int _acked;

            public Task SendAsync(BifrostMessage frame, CancellationToken cancellationToken)
            {
                lock (Sent)
                {
                    Sent.Add(frame);
                    MaxInFlightSeen = Math.Max(MaxInFlightSeen, Sent.Count - _acked);
                }
                _received.Writer.TryWrite(frame);
                return Task.CompletedTask;
            }

            public async Task AnswerAsync(ChunkTransfer transfer, CancellationToken cancellationToken)
            {
                if (Silent)
                    return;
                var index = 0;
                await foreach (var chunk in _received.Reader.ReadAllAsync(cancellationToken))
                {
                    var received = index++;
                    if (BeforeEachReply != null)
                        await BeforeEachReply();
                    if (ExtraBefore.TryGetValue(received, out var extras))
                        foreach (var extra in extras)
                            transfer.Acknowledge(extra);

                    var reply = NackOnly
                        ? new BifrostMessage { RequestId = chunk.RequestId, Type = BifrostMessageType.ChunkNack, ChunkSequence = 0 }
                        : NackInsteadOfAck.Contains(received)
                            ? new BifrostMessage { RequestId = chunk.RequestId, Type = BifrostMessageType.ChunkNack, ChunkSequence = chunk.ChunkSequence }
                            : ChunkReceiver.CreateAck(chunk.RequestId, chunk.ChunkSequence);
                    lock (Sent) _acked++;
                    transfer.Acknowledge(reply);
                }
            }

            public void Done() => _received.Writer.TryComplete();
        }

        private static async Task RunAsync(ChunkSender sender, BifrostMessage response, ScriptedClient client, CancellationToken token = default)
        {
            var transfer = sender.StartTransfer(sender.SplitIntoChunks(response), client.SendAsync);
            var answering = client.AnswerAsync(transfer, CancellationToken.None);
            try
            {
                await transfer.RunAsync(token);
            }
            finally
            {
                client.Done();
                await answering;
            }
        }

        private static BifrostMessage Nack(uint requestId, uint sequence)
            => new() { RequestId = requestId, Type = BifrostMessageType.ChunkNack, ChunkSequence = sequence };

        [Fact]
        public async Task SmallResult_IsOneChunk_AndWaitsForItsAck()
        {
            var sender = new ChunkSender(); // default 64KB threshold
            var client = new ScriptedClient();

            await RunAsync(sender, Result(1, 100), client);

            client.Sent.Should().ContainSingle().Which.Type.Should().Be(BifrostMessageType.Chunk);
        }

        [Fact]
        public async Task LargePayload_AllChunksSent_AndReassembleToOriginal()
        {
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 8);
            var client = new ScriptedClient();
            var response = Result(42, 1000);

            await RunAsync(sender, response, client);

            var sent = client.Sent;
            sent.Should().HaveCountGreaterThanOrEqualTo(10);
            sent.Should().OnlyContain(m => m.Type == BifrostMessageType.Chunk);
            for (var i = 0; i < sent.Count; i++)
                sent[i].ChunkSequence.Should().Be((uint)i);

            var receiver = new ChunkReceiver();
            byte[]? assembled = null;
            foreach (var frame in sent)
                assembled = receiver.AddChunk(BifrostMessage.FromBytes(frame.ToBytes()));

            assembled.Should().NotBeNull();
            var restored = BifrostMessage.FromBytes(assembled!);
            restored.RequestId.Should().Be(42);
            restored.Type.Should().Be(BifrostMessageType.Result);
            restored.Payload.Should().BeEquivalentTo(response.Payload);
        }

        [Fact]
        public async Task Window_BoundsTheUnacknowledgedChunks()
        {
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 2);
            var client = new ScriptedClient();

            await RunAsync(sender, Result(5, 1000), client); // ~11 chunks

            client.Sent.Should().HaveCountGreaterThan(2);
            client.MaxInFlightSeen.Should().Be(2, "the transfer pauses once the window of 2 is unacknowledged");
        }

        [Fact]
        public async Task WithBuffer_StoresEveryChunkForResumption()
        {
            var buffer = new ChunkBuffer();
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 8, chunkBuffer: buffer);
            var client = new ScriptedClient();

            await RunAsync(sender, Result(7, 500), client);

            buffer.Contains(7).Should().BeTrue();
            for (uint seq = 0; seq < (uint)client.Sent.Count; seq++)
                buffer.TryGet(7, seq).Should().NotBeNull();
        }

        [Fact]
        public async Task Nack_RetransmitsTheRequestedChunk()
        {
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 2, chunkBuffer: new ChunkBuffer());
            var client = new ScriptedClient();
            client.ExtraBefore[1] = new() { Nack(11, 0) };

            await RunAsync(sender, Result(11, 1000), client);

            client.Sent.Count(m => m.ChunkSequence == 0).Should().Be(2,
                "chunk 0 is sent once and once more on the NACK");
        }

        [Fact]
        public async Task NackOnFinalChunk_IsHonoredDuringTailDrain()
        {
            // With a window wider than the chunk count every chunk goes out without a wait;
            // the transfer then drains the outstanding acks, so a NACK on the last sequence
            // is still served before the transfer completes.
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 64, chunkBuffer: new ChunkBuffer());
            var client = new ScriptedClient();
            var chunkCount = sender.SplitIntoChunks(Result(21, 500)).Count;
            var finalSeq = (uint)(chunkCount - 1);
            client.ExtraBefore[chunkCount - 1] = new() { Nack(21, finalSeq) };

            await RunAsync(sender, Result(21, 500), client);

            client.Sent.Count(m => m.ChunkSequence == finalSeq).Should().Be(2,
                "the final chunk is sent once and retransmitted on the tail NACK");
        }

        [Theory]
        [InlineData(64)] // every chunk out at once; the NACK lands in the tail drain
        [InlineData(2)]  // the NACK lands while the window is full
        public async Task NackInsteadOfAck_TheAckedRetransmitCompletesTheTransfer(int ackWindow)
        {
            // A client that finds chunk 1 corrupt NACKs it and never acks the original; it acks
            // the retransmit. The NACK used to count as one more owed ack, so the transfer waited
            // for an ack that never came and failed on the ack timeout.
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow, chunkBuffer: new ChunkBuffer(),
                ackTimeout: TimeSpan.FromSeconds(30));
            var client = new ScriptedClient();
            client.NackInsteadOfAck.Add(1);

            var run = RunAsync(sender, Result(31, 600), client);

            (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)))).Should().BeSameAs(run,
                "the acked retransmit answers chunk 1, so the transfer finishes without waiting out the ack timeout");
            await run;
            client.Sent.Count(m => m.ChunkSequence == 1).Should().Be(2, "chunk 1 is sent once and retransmitted once");
        }

        [Fact]
        public async Task AckOfBothCopies_DoesNotEndTheTransferEarly()
        {
            // The NACK of chunk 0 comes with an ack of the original too, and the retransmit is
            // acked again. Only distinct sequences count, so the transfer still waits for every
            // other chunk's ack and sends all of them.
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 2, chunkBuffer: new ChunkBuffer());
            var client = new ScriptedClient();
            client.ExtraBefore[1] = new() { Nack(32, 0), ChunkReceiver.CreateAck(32, 0) };
            var chunkCount = sender.SplitIntoChunks(Result(32, 600)).Count;

            await RunAsync(sender, Result(32, 600), client);

            client.Sent.Select(m => m.ChunkSequence).Distinct().Should().HaveCount(chunkCount);
        }

        [Fact]
        public async Task NackForASequenceTheTransferDoesNotHave_IsIgnored()
        {
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 2);
            var client = new ScriptedClient();
            client.ExtraBefore[0] = new() { Nack(12, 9999) };

            await RunAsync(sender, Result(12, 600), client);

            client.Sent.Should().OnlyHaveUniqueItems(m => m.ChunkSequence);
        }

        [Fact]
        public async Task ClientNeverAcks_ThrowsTimeoutInsteadOfBlockingForever()
        {
            var sender = new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: null,
                ackTimeout: TimeSpan.FromMilliseconds(100));
            var client = new ScriptedClient { Silent = true };

            var act = () => RunAsync(sender, Result(13, 1000), client);

            await act.Should().ThrowAsync<TimeoutException>();
        }

        [Fact]
        public async Task ExternalCancellation_ThrowsCancellation_NotTimeout()
        {
            var sender = new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: null,
                ackTimeout: TimeSpan.FromSeconds(30));
            var client = new ScriptedClient { Silent = true };
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            var act = () => RunAsync(sender, Result(14, 1000), client, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task PerpetualNack_ExceedsRetransmitCap_AbortsTransfer()
        {
            var sender = new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: new ChunkBuffer(),
                ackTimeout: TimeSpan.FromSeconds(30), logger: null, maxRetransmitsPerSequence: 3);
            var client = new ScriptedClient { NackOnly = true };

            var act = () => RunAsync(sender, Result(20, 1000), client);

            (await act.Should().ThrowAsync<ChunkRetransmitLimitExceededException>())
                .Which.Sequence.Should().Be(0u);
        }

        [Fact]
        public async Task NacksUnderCap_StillComplete()
        {
            var sender = new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: new ChunkBuffer(),
                ackTimeout: TimeSpan.FromSeconds(30), logger: null, maxRetransmitsPerSequence: 3);
            var client = new ScriptedClient();
            client.ExtraBefore[1] = new() { Nack(21, 0) };
            client.ExtraBefore[2] = new() { Nack(21, 0) };
            client.ExtraBefore[3] = new() { Nack(21, 0) };

            await RunAsync(sender, Result(21, 1000), client);

            client.Sent.Count(m => m.ChunkSequence == 0).Should().Be(4,
                "chunk 0 is sent once plus three NACK retransmissions");
        }

        [Fact]
        public async Task AckAfterTheTransferEnded_IsDropped_WithoutBlocking()
        {
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 2);
            var client = new ScriptedClient();
            var transfer = sender.StartTransfer(sender.SplitIntoChunks(Result(30, 300)), client.SendAsync);
            var answering = client.AnswerAsync(transfer, CancellationToken.None);
            await transfer.RunAsync(CancellationToken.None);
            client.Done();
            await answering;

            // Far more late acks than the channel holds: none may block the reader.
            var late = Task.Run(() =>
            {
                for (var i = 0; i < 1000; i++)
                    transfer.Acknowledge(ChunkReceiver.CreateAck(30, 0));
            });

            (await Task.WhenAny(late, Task.Delay(TimeSpan.FromSeconds(5)))).Should().BeSameAs(late);
        }

        [Fact]
        public async Task AckFlood_IsRefused_WithoutBlockingTheReader()
        {
            // The reader routes acks into a bounded channel (4 x the ack window). It used to wait
            // when the channel was full; a reader waiting there reads nothing more, so neither the
            // idle deadline nor a close ever reaches it. A transfer parked in its first send
            // consumes nothing, so the ninth ack overflows a window-2 channel.
            var sender = new ChunkSender(chunkThreshold: 100, ackWindow: 2);
            var parked = new TaskCompletionSource();
            var transfer = sender.StartTransfer(
                sender.SplitIntoChunks(Result(31, 300)), (_, _) => parked.Task);
            using var stop = new CancellationTokenSource();
            var running = transfer.RunAsync(stop.Token);

            var accepted = 0;
            var flood = Task.Run(() =>
            {
                for (var i = 0; i < 9; i++)
                {
                    transfer.Acknowledge(ChunkReceiver.CreateAck(31, 0));
                    accepted++;
                }
            });

            await flood.Awaiting(t => t.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().ThrowAsync<InvalidOperationException>();
            accepted.Should().Be(8, "control: acks up to the channel bound are taken");

            // The connection ending stops the transfer; it never got the acks it needs.
            stop.Cancel();
            parked.SetResult();
            await running.Awaiting(t => t.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public void Constructor_RejectsNonPositiveAckTimeout()
        {
            var act = () => new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: null, ackTimeout: TimeSpan.Zero);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Constructor_AcceptsNoAckTimeout()
        {
            var act = () => new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: null, ackTimeout: ChunkSender.NoAckTimeout);
            act.Should().NotThrow();
        }

        /// <summary>
        /// Moves the injected clock forward one second at a time, yielding to the transfer between
        /// steps so its wait (and any timer it arms) is in place before the clock passes it.
        /// </summary>
        private static async Task AdvanceAsync(Microsoft.Extensions.Time.Testing.FakeTimeProvider clock, TimeSpan total)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += TimeSpan.FromSeconds(1))
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(1);
            }
        }

        [Fact]
        public async Task SlowConsumer_TakingMinutesPerChunk_ReceivesTheWholeTransfer()
        {
            // The client's consumer holds every chunk for 61 s of injected time before acking,
            // twice the 30 s ack timeout that used to close the connection. With no stated ack
            // timeout a full window is backpressure: the transfer waits and then completes.
            var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
            var sender = new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: new ChunkBuffer(), timeProvider: clock);
            var client = new ScriptedClient { BeforeEachReply = () => AdvanceAsync(clock, TimeSpan.FromSeconds(61)) };
            var response = Result(40, 600);
            var chunkCount = sender.SplitIntoChunks(response).Count;

            var run = RunAsync(sender, response, client);

            (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(run);
            await run;
            client.Sent.Select(m => m.ChunkSequence).Should().Equal(Enumerable.Range(0, chunkCount).Select(i => (uint)i),
                "every chunk goes out once, in order, paced by the slow consumer's acks");
        }

        [Fact]
        public async Task StatedAckTimeout_RunsOnTheInjectedClock()
        {
            // A mount that states an ack timeout still gets one, on the injected clock: the real
            // clock never gets near 30 s here.
            var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
            var sender = new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: null,
                ackTimeout: TimeSpan.FromSeconds(30), timeProvider: clock);
            var client = new ScriptedClient { BeforeEachReply = () => AdvanceAsync(clock, TimeSpan.FromSeconds(35)) };

            var act = () => RunAsync(sender, Result(41, 600), client);

            (await act.Should().ThrowAsync<TimeoutException>().WaitAsync(TimeSpan.FromSeconds(30)))
                .Which.Message.Should().Contain("30 seconds");
        }

        [Fact]
        public void Constructor_RejectsNonPositiveRetransmitCap()
        {
            var act = () => new ChunkSender(
                chunkThreshold: 100, ackWindow: 2, chunkBuffer: null,
                maxRetransmitsPerSequence: 0);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
