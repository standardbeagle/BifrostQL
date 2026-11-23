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
            int ackWindow = 64, TimeSpan? ackTimeout = null, TimeProvider? timeProvider = null,
            TimeSpan idleTimeout = default)
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
                idleTimeout: idleTimeout,
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
            // The idle deadline runs on the same fake clock, and the loop below can advance it by
            // thousands of fake seconds before the ack timer is armed: set it far beyond that
            // budget, so only the ack timeout can end the response.
            var middleware = CreateMiddleware(ackWindow: 2, ackTimeout: TimeSpan.FromSeconds(30), timeProvider: clock,
                idleTimeout: TimeSpan.FromHours(24));
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
            // The ack timer starts only after the second chunk's send returns, which can lag the
            // Sent event under load; a fixed number of steps could finish before the timer exists.
            // Keep the fake clock moving until the response is abandoned, bounded by wall time.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!abandoned.Task.IsCompleted && DateTime.UtcNow < deadline)
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

        /// <summary>
        /// Large result for <c>{ blob }</c> (chunked at threshold 100), a small one for anything
        /// else, so a second request on a connection that owes acks answers unchunked.
        /// </summary>
        private sealed class MixedResultEngine : IBifrostEngine
        {
            public Task<BifrostResult> ExecuteAsync(BifrostRequest request, string endpointPath)
                => Task.FromResult(new BifrostResult
                {
                    Data = request.Query.Contains("blob", StringComparison.Ordinal)
                        ? new Dictionary<string, object?> { ["blob"] = new string('x', 2000) }
                        : new Dictionary<string, object?> { ["ping"] = "pong" },
                });
        }

        /// <summary>
        /// Builds the pipeline the production mount builds: <c>UseBifrostBinary</c> over a
        /// container that registers <paramref name="clock"/> as the host's
        /// <see cref="TimeProvider"/>. Only the ack timeout argument varies between callers;
        /// null leaves it unstated, as a host that never mentions it does.
        /// </summary>
        private static (RequestDelegate Pipeline, IServiceProvider Services) MountBinary(
            TimeProvider clock, TimeSpan? ackTimeout)
        {
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IBifrostEngine>(new MixedResultEngine())
                .AddSingleton(clock)
                .BuildServiceProvider();
            var app = new Microsoft.AspNetCore.Builder.ApplicationBuilder(services);
            if (ackTimeout is { } stated)
                app.UseBifrostBinary(chunkThreshold: 100, ackWindow: 2, requireAuthentication: false, ackTimeout: stated);
            else
                app.UseBifrostBinary(chunkThreshold: 100, ackWindow: 2, requireAuthentication: false);
            return (app.Build(), services);
        }

        private static Task RunMountedAsync(RequestDelegate pipeline, IServiceProvider services, FakeWebSocket socket)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Path = "/bifrost-ws";
            context.Features.Set<IHttpWebSocketFeature>(new FakeWebSocketFeature(socket));
            return pipeline(context);
        }

        [Fact]
        public async Task DefaultMount_AClientThatStopsAcking_GetsChunkAckTimeoutAfterSixtySeconds_AndTheConnectionServesOn()
        {
            // UseBifrostBinary with no ackTimeout: the stalled response is bounded by
            // ChunkSender.DefaultAckTimeout (60 s) on the host's clock. It must not fire one
            // second early, must fire once 60 s pass, and must leave the connection serving.
            // The real wait budget (5 s per await) is far below 60 s, so a mount that ran the
            // timer on TimeProvider.System instead of the injected clock never answers here.
            var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
            var (pipeline, services) = MountBinary(clock, ackTimeout: null);
            var windowFull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var abandoned = new TaskCompletionSource<BifrostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pong = new TaskCompletionSource<BifrostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var socket = new FakeWebSocket { HangWhenDrained = true };
            var chunksSent = 0;
            socket.Sent += m =>
            {
                if (m.Type == BifrostMessageType.Chunk && Interlocked.Increment(ref chunksSent) == 2)
                    windowFull.TrySetResult();
                if (m.Type == BifrostMessageType.Error)
                    abandoned.TrySetResult(m);
                if (m.Type == BifrostMessageType.Result && m.RequestId == 12)
                    pong.TrySetResult(m);
            };
            socket.EnqueueMessage(Query(11));

            var run = RunMountedAsync(pipeline, services, socket);
            await windowFull.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // 59 s, one second at a time: still waiting.
            for (var i = 0; i < 59; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(1);
            }
            abandoned.Task.IsCompleted.Should().BeFalse("the default ack timeout is 60 s, not less");

            // Past 60 s: the stalled response is abandoned with the coded error.
            for (var i = 0; i < 5 && !abandoned.Task.IsCompleted; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(1);
            }
            var error = await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            error.RequestId.Should().Be(11u);
            error.ErrorCode.Should().Be(BinaryErrorCode.ChunkAckTimeout);
            socket.State.Should().Be(WebSocketState.Open, "only the stalled response is abandoned");
            socket.ClosedWith.Should().BeNull();

            // The same connection still answers a new request.
            socket.DeliverMessage(new BifrostMessage { RequestId = 12, Type = BifrostMessageType.Query, Query = "{ ping }" });
            var answer = await pong.Task.WaitAsync(TimeSpan.FromSeconds(5));
            answer.Errors.Should().BeEmpty();

            socket.DeliverClose();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            socket.ClosedWith.Should().Be(WebSocketCloseStatus.NormalClosure);
        }

        [Fact]
        public async Task ExplicitNoAckTimeout_Mount_AClientThatStopsAcking_IsNeverTimedOut()
        {
            // Control for the default: the same mount with the explicit opt-out waits as long as
            // the consumer takes, however far the clock moves (here twice the default).
            var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
            var (pipeline, services) = MountBinary(clock, ChunkSender.NoAckTimeout);
            var windowFull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var socket = new FakeWebSocket { HangWhenDrained = true };
            var chunksSent = 0;
            socket.Sent += m =>
            {
                if (m.Type == BifrostMessageType.Chunk && Interlocked.Increment(ref chunksSent) == 2)
                    windowFull.TrySetResult();
            };
            socket.EnqueueMessage(Query(6));

            var run = RunMountedAsync(pipeline, services, socket);
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
