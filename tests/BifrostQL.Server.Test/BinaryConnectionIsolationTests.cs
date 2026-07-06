using System.Net.WebSockets;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// End-to-end middleware tests for the binary WebSocket transport's chunk-buffer
    /// lifecycle: connection-scoped isolation (no cross-connection Resume/ChunkNack
    /// replay of another principal's buffered results), buffer release on transfer
    /// completion, and connection abort when a client stops acknowledging chunks.
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
            int ackWindow = 64, TimeSpan? ackTimeout = null)
        {
            return new BifrostBinaryMiddleware(
                _ => Task.CompletedTask,
                new LargeResultEngine(),
                "/ws",
                NullLogger<BifrostBinaryMiddleware>.Instance,
                chunkThreshold: 100,
                ackWindow: ackWindow,
                ackTimeout: ackTimeout ?? ChunkSender.DefaultAckTimeout);
        }

        private static Task RunConnectionAsync(BifrostBinaryMiddleware middleware, FakeWebSocket socket)
        {
            var context = new DefaultHttpContext();
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
            var resumeAck = sent.Single(m => m.Type == BifrostMessageType.ResumeAck);
            resumeAck.ChunkTotal.Should().Be(0u, "a completed transfer must not remain buffered");
        }

        [Fact]
        public async Task ClientThatNeverAcks_GetsConnectionClosed_InsteadOfWedgingServer()
        {
            // Small ack window forces the sender to wait for acks; the client never
            // sends any (queue drained + HangWhenDrained), so the ack timeout must
            // abort the transfer and close the connection.
            var middleware = CreateMiddleware(ackWindow: 2, ackTimeout: TimeSpan.FromMilliseconds(100));

            var socket = new FakeWebSocket { HangWhenDrained = true };
            socket.EnqueueMessage(Query(5));

            await RunConnectionAsync(middleware, socket).WaitAsync(TimeSpan.FromSeconds(5));

            socket.State.Should().Be(WebSocketState.Closed, "the server must close the wedged connection");
        }
    }
}
