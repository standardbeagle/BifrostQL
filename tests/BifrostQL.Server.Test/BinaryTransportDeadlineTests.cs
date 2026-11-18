using System.Net.WebSockets;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Deadlines of the binary WebSocket transport that a hostile or broken peer can otherwise
    /// dodge: the write deadline (a peer that stops reading) and the injected-clock read
    /// deadline. Every timeout runs on a <see cref="FakeTimeProvider"/> and is set far beyond
    /// the real wait budget, so a mutant on the system clock fails by timing out.
    /// </summary>
    public class BinaryTransportDeadlineTests
    {
        /// <summary>Real-time budget for anything the fake clock should release at once.</summary>
        private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan FarTimeout = TimeSpan.FromMinutes(10);

        private sealed class NullEngine : IBifrostEngine
        {
            public Task<BifrostResult> ExecuteAsync(BifrostRequest request, string endpointPath)
                => Task.FromResult(new BifrostResult { Data = null });
        }

        private sealed class PassthroughFeature : IHttpWebSocketFeature
        {
            private readonly WebSocket _socket;
            public PassthroughFeature(WebSocket socket) => _socket = socket;
            public bool IsWebSocketRequest => true;
            public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(_socket);
        }

        private static BifrostBinaryMiddleware Middleware(
            TimeProvider clock,
            TimeSpan firstFrameTimeout = default,
            TimeSpan idleTimeout = default,
            TimeSpan writeTimeout = default)
            => new(
                next: _ => Task.CompletedTask,
                engine: new NullEngine(),
                endpointPath: "/ws",
                logger: NullLogger<BifrostBinaryMiddleware>.Instance,
                chunkThreshold: ChunkSender.DefaultChunkThreshold,
                ackWindow: ChunkSender.DefaultAckWindow,
                ackTimeout: ChunkSender.NoAckTimeout,
                requireAuthenticatedIdentity: false,
                firstFrameTimeout: firstFrameTimeout,
                idleTimeout: idleTimeout,
                writeTimeout: writeTimeout,
                timeProvider: clock);

        private static HttpContext ContextFor(WebSocket socket)
        {
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpWebSocketFeature>(new PassthroughFeature(socket));
            return context;
        }

        /// <summary>The first chunk of a two-chunk upload: valid, so the reader acks it.</summary>
        private static BifrostMessage FirstUploadChunk(uint requestId)
        {
            var payload = new byte[] { 1, 2, 3, 4 };
            return new BifrostMessage
            {
                RequestId = requestId,
                Type = BifrostMessageType.Chunk,
                Payload = payload,
                ChunkSequence = 0,
                ChunkTotal = 2,
                ChunkOffset = 0,
                TotalBytes = 8,
                ChunkChecksum = ChunkSender.ComputeCrc32(payload),
            };
        }

        [Fact]
        public async Task PeerThatStopsReading_IsAbortedOnTheWriteDeadline_AndReleasesItsSlot()
        {
            // The reader's own frame (the ack of an upload chunk) parks on a peer that reads
            // nothing. Before the write deadline the reader sat in that send for ever: it never
            // reached the next read, so the idle deadline never ran and the slot stayed taken.
            var clock = new FakeTimeProvider();
            var socket = new FakeWebSocket { Pipelined = true, HangWhenDrained = true, StallAfterSends = 0 };
            socket.EnqueueMessage(FirstUploadChunk(requestId: 1));
            var middleware = Middleware(clock, idleTimeout: FarTimeout, writeTimeout: FarTimeout);

            var connection = middleware.InvokeAsync(ContextFor(socket));
            await socket.SendStalled.WaitAsync(WaitBudget);
            middleware.ActiveConnections.Should().Be(1, "the stalled connection holds its slot until the deadline");

            clock.Advance(FarTimeout);
            await connection.WaitAsync(WaitBudget);

            middleware.ActiveConnections.Should().Be(0, "a write past its deadline ends the connection and frees the slot");
            socket.State.Should().Be(WebSocketState.Aborted,
                "a peer that takes no frame cannot take a close frame either, so the socket is aborted");
        }
    }
}
