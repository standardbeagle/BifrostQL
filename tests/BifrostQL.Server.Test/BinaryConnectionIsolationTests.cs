using System.Net.WebSockets;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// End-to-end middleware tests for the binary WebSocket transport's chunk-buffer
    /// lifecycle: connection-scoped isolation (no cross-connection Resume/ChunkNack
    /// replay of another principal's buffered results), buffer release on transfer
    /// completion, and what a client that stops acknowledging chunks gets.
    /// </summary>
    public class BinaryConnectionIsolationTests
    {
        /// <summary>Engine stub returning a payload large enough to force chunking at threshold 100.</summary>
        private sealed class LargeResultEngine : IBifrostEngine
        {
            public Task<BifrostResult> ExecuteAsync(BifrostRequest request, string endpointPath)
                => Task.FromResult(new BifrostResult
                {
                    Data = new Dictionary<string, object?> { ["blob"] = new string('x', 2000) },
                });
        }

        private sealed class FakeWebSocketFeature : IHttpWebSocketFeature
        {
            private readonly WebSocket _socket;
            public FakeWebSocketFeature(WebSocket socket) => _socket = socket;
            public bool IsWebSocketRequest => true;
            public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(_socket);
        }

        private static BifrostBinaryMiddleware CreateMiddleware(
            int ackWindow = 64, TimeSpan? ackTimeout = null, TimeProvider? timeProvider = null)
        {
            return new BifrostBinaryMiddleware(
                _ => Task.CompletedTask,
                new LargeResultEngine(),
                "/ws",
                NullLogger<BifrostBinaryMiddleware>.Instance,
                chunkThreshold: 100,
                ackWindow: ackWindow,
                ackTimeout: ackTimeout ?? ChunkSender.NoAckTimeout,
                // Transport mechanics under an anonymous caller: this mount states the open
                // posture explicitly, the way a DisableAuth endpoint's mount does.
                requireAuthenticatedIdentity: false,
                timeProvider: timeProvider);
        }

        private static Task RunConnectionAsync(BifrostBinaryMiddleware middleware, FakeWebSocket socket)
        {
            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
            context.Features.Set<IHttpWebSocketFeature>(new FakeWebSocketFeature(socket));
            return middleware.InvokeAsync(context);
        }

        private static BifrostMessage Query(uint requestId) => new()
        {
            RequestId = requestId,
            Type = BifrostMessageType.Query,
            Query = "{ blob }",
        };

        [Fact]
        public async Task Resume_FromAnotherConnection_DoesNotLeakBufferedChunks()
        {
            var middleware = CreateMiddleware();

            // Connection A: query produces a chunked response, but the socket dies
            // after 3 frames — the transfer is interrupted, chunks stay buffered.
            var socketA = new FakeWebSocket { FailAfterSends = 3 };
            socketA.EnqueueMessage(Query(7));
            await RunConnectionAsync(middleware, socketA).WaitAsync(TimeSpan.FromSeconds(5));
            socketA.SentMessages().Should().Contain(m => m.Type == BifrostMessageType.Chunk,
                "the interrupted transfer must have started chunking");

            // Connection B: replays A's request_id via Resume. It must get nothing.
            var socketB = new FakeWebSocket();
            socketB.EnqueueMessage(new BifrostMessage
            {
                RequestId = 7,
                Type = BifrostMessageType.Resume,
                LastSequence = uint.MaxValue,
            });
            socketB.EnqueueClose();
            await RunConnectionAsync(middleware, socketB).WaitAsync(TimeSpan.FromSeconds(5));

            var sentB = socketB.SentMessages();
            sentB.Should().NotContain(m => m.Type == BifrostMessageType.Chunk,
                "connection B must never receive connection A's buffered chunks");
            var resumeAck = sentB.Single(m => m.Type == BifrostMessageType.ResumeAck);
            resumeAck.ChunkTotal.Should().Be(0u, "the transfer is unknown on this connection");
        }

        [Fact]
        public async Task ChunkNack_FromAnotherConnection_DoesNotLeakBufferedChunks()
        {
            var middleware = CreateMiddleware();

            var socketA = new FakeWebSocket { FailAfterSends = 3 };
            socketA.EnqueueMessage(Query(9));
            await RunConnectionAsync(middleware, socketA).WaitAsync(TimeSpan.FromSeconds(5));

            var socketB = new FakeWebSocket();
            socketB.EnqueueMessage(new BifrostMessage
            {
                RequestId = 9,
                Type = BifrostMessageType.ChunkNack,
                ChunkSequence = 0,
            });
            socketB.EnqueueClose();
            await RunConnectionAsync(middleware, socketB).WaitAsync(TimeSpan.FromSeconds(5));

            var sentB = socketB.SentMessages();
            sentB.Should().NotContain(m => m.Type == BifrostMessageType.Chunk);
            sentB.Should().Contain(m => m.Type == BifrostMessageType.Error,
                "an unknown chunk must be answered with an error, not another connection's data");
        }

        [Fact]
        public async Task CompletedTransfer_IsReleasedFromBuffer_ResumeReturnsNothing()
        {
            var middleware = CreateMiddleware();

            // Same connection: full transfer completes, then the client sends Resume for
            // the finished request. The buffer must already be released (no TTL lingering).
            var socket = new FakeWebSocket();
            socket.EnqueueMessage(Query(3));
            socket.EnqueueMessage(new BifrostMessage
            {
                RequestId = 3,
                Type = BifrostMessageType.Resume,
                LastSequence = uint.MaxValue,
            });
            socket.EnqueueClose();
            await RunConnectionAsync(middleware, socket).WaitAsync(TimeSpan.FromSeconds(5));

            var sent = socket.SentMessages();
            sent.Should().Contain(m => m.Type == BifrostMessageType.Chunk, "the response was chunked");
            sent.Should().ContainSingle(m => m.Type == BifrostMessageType.ResumeAck,
                "the Resume is answered; sent: " + string.Join(", ", sent.Select(m => $"{m.Type}#{m.RequestId}/{m.ChunkSequence} {string.Join('|', m.Errors)}")));
            var resumeAck = sent.Single(m => m.Type == BifrostMessageType.ResumeAck);
            resumeAck.ChunkTotal.Should().Be(0u, "a completed transfer must not remain buffered");
        }

        [Fact]
        public async Task StatedAckTimeout_AbandonsOnlyTheStalledResponse_WithACodedError()
        {
            // A mount that states a 30 s ack timeout, a client that never acks: the response is
            // abandoned with a CHUNK_ACK_TIMEOUT Error frame for its request, and the connection
            // stays open (it used to be closed with PolicyViolation and no frame saying why).
            var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
            var middleware = CreateMiddleware(ackWindow: 2, ackTimeout: TimeSpan.FromSeconds(30), timeProvider: clock);
            var windowFull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var abandoned = new TaskCompletionSource<BifrostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var socket = new FakeWebSocket { HangWhenDrained = true };
            var chunksSent = 0;
            socket.Sent += m =>
            {
                if (m.Type == BifrostMessageType.Chunk && Interlocked.Increment(ref chunksSent) == 2)
                    windowFull.TrySetResult();
                if (m.Type == BifrostMessageType.Error)
                    abandoned.TrySetResult(m);
            };
            socket.EnqueueMessage(Query(5));

            var run = RunConnectionAsync(middleware, socket);
            await windowFull.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 35 && !abandoned.Task.IsCompleted; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(1);
            }

            var error = await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            error.RequestId.Should().Be(5u);
            error.ErrorCode.Should().Be(BinaryErrorCode.ChunkAckTimeout);
            socket.State.Should().Be(WebSocketState.Open, "only the stalled response is abandoned");
            socket.ClosedWith.Should().BeNull();

            socket.DeliverClose();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            socket.ClosedWith.Should().Be(WebSocketCloseStatus.NormalClosure);
        }

        [Fact]
        public async Task NoStatedAckTimeout_AClientThatStopsAcking_IsNotDisconnected()
        {
            // The default mount has no ack timer: a full window pauses the transfer for as long as
            // the consumer takes, however far the clock moves.
            var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
            var middleware = CreateMiddleware(ackWindow: 2, timeProvider: clock);
            var windowFull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var socket = new FakeWebSocket { HangWhenDrained = true };
            var chunksSent = 0;
            socket.Sent += m =>
            {
                if (m.Type == BifrostMessageType.Chunk && Interlocked.Increment(ref chunksSent) == 2)
                    windowFull.TrySetResult();
            };
            socket.EnqueueMessage(Query(6));

            var run = RunConnectionAsync(middleware, socket);
            await windowFull.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 120; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(1);
            }

            socket.SentMessages().Should().NotContain(m => m.Type == BifrostMessageType.Error);
            socket.State.Should().Be(WebSocketState.Open);

            socket.DeliverClose();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
