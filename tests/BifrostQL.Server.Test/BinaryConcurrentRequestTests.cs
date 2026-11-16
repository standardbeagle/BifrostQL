using System.Collections.Concurrent;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.WebSockets;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Requests on one binary connection run concurrently: a slow or chunked response does not
    /// hold up the requests behind it, a reused in-flight request id is refused, and a
    /// connection runs at most <see cref="BifrostBinaryMiddleware.MaxConcurrentRequestsPerConnection"/>.
    /// </summary>
    public class BinaryConcurrentRequestTests
    {
        /// <summary>
        /// "{ slow }" waits on <see cref="Release"/>; "{ fast }" answers at once and releases the
        /// slow ones; "{ large }" answers with a payload that chunks at threshold 100.
        /// </summary>
        private sealed class GatedEngine : IBifrostEngine
        {
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int SlowEntered;

            public async Task<BifrostResult> ExecuteAsync(BifrostRequest request, string endpointPath)
            {
                switch (request.Query)
                {
                    case "{ slow }":
                        Interlocked.Increment(ref SlowEntered);
                        await Release.Task.WaitAsync(request.CancellationToken);
                        return Data("slow", "done");
                    case "{ fast }":
                        Release.TrySetResult();
                        return Data("fast", "done");
                    case "{ large }":
                        return Data("blob", new string('x', 2000));
                    default:
                        throw new InvalidOperationException("unexpected query " + request.Query);
                }
            }

            private static BifrostResult Data(string field, string value)
                => new() { Data = new Dictionary<string, object?> { [field] = value } };
        }

        private sealed class SocketFeature : IHttpWebSocketFeature
        {
            private readonly WebSocket _socket;
            public SocketFeature(WebSocket socket) => _socket = socket;
            public bool IsWebSocketRequest => true;
            public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(_socket);
        }

        private static Task RunAsync(IBifrostEngine engine, FakeWebSocket socket, int ackWindow = 64)
        {
            var middleware = new BifrostBinaryMiddleware(
                _ => Task.CompletedTask,
                engine,
                "/ws",
                NullLogger<BifrostBinaryMiddleware>.Instance,
                chunkThreshold: 100,
                ackWindow: ackWindow,
                ackTimeout: ChunkSender.DefaultAckTimeout,
                requireAuthenticatedIdentity: false);
            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
            context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
            return middleware.InvokeAsync(context).WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static BifrostMessage Query(uint requestId, string query)
            => new() { RequestId = requestId, Type = BifrostMessageType.Query, Query = query };

        /// <summary>Reassembles the chunked response for one request from the frames sent.</summary>
        private static BifrostMessage Reassemble(IEnumerable<BifrostMessage> sent, uint requestId)
        {
            var receiver = new ChunkReceiver();
            byte[]? assembled = null;
            foreach (var chunk in sent.Where(m => m.RequestId == requestId && m.Type == BifrostMessageType.Chunk))
                assembled = receiver.AddChunk(chunk) ?? assembled;
            assembled.Should().NotBeNull($"request {requestId} must have received every chunk");
            return BifrostMessage.FromBytes(assembled!);
        }

        [Fact]
        public async Task SlowRequest_DoesNotBlockTheRequestPipelinedBehindIt()
        {
            // The slow request only finishes once the fast one has run, so a connection that
            // serves one request at a time never answers either.
            var engine = new GatedEngine();
            var socket = new FakeWebSocket { Pipelined = true };
            socket.EnqueueMessage(Query(1, "{ slow }"));
            socket.EnqueueMessage(Query(2, "{ fast }"));

            await RunAsync(engine, socket);

            var sent = socket.SentMessages();
            sent.Should().NotContain(m => m.Type == BifrostMessageType.Error);
            var fast = sent.FindIndex(m => m.RequestId == 2 && m.Type == BifrostMessageType.Result);
            var slow = sent.FindIndex(m => m.RequestId == 1 && m.Type == BifrostMessageType.Result);
            fast.Should().BeGreaterThanOrEqualTo(0);
            slow.Should().BeGreaterThan(fast, "the slow request answers after the one it waited on");
        }

        [Fact]
        public async Task QueryPipelinedDuringAChunkedTransfer_IsServed()
        {
            // A window of 2 keeps request 1's transfer waiting on acks while request 2 arrives.
            var engine = new GatedEngine();
            var socket = new FakeWebSocket { Pipelined = true };
            socket.EnqueueMessage(Query(1, "{ large }"));
            socket.EnqueueMessage(Query(2, "{ large }"));

            await RunAsync(engine, socket, ackWindow: 2);

            var sent = socket.SentMessages();
            sent.Should().NotContain(m => m.Type == BifrostMessageType.Error,
                "a query pipelined during a chunked transfer is served, not refused");
            Reassemble(sent, 1).Payload.Should().Equal(Reassemble(sent, 2).Payload);
            Reassemble(sent, 1).Type.Should().Be(BifrostMessageType.Result);
        }

        [Fact]
        public async Task ReusedInFlightRequestId_IsRefused_AndTheFirstStillAnswers()
        {
            var engine = new GatedEngine();
            var socket = new FakeWebSocket { Pipelined = true };
            socket.EnqueueMessage(Query(5, "{ slow }"));
            socket.EnqueueMessage(Query(5, "{ fast }"));
            socket.EnqueueMessage(Query(6, "{ fast }"));

            await RunAsync(engine, socket);

            var sent = socket.SentMessages().Where(m => m.RequestId == 5).ToList();
            sent.Should().ContainSingle(m => m.Type == BifrostMessageType.Error)
                .Which.Errors.Should().ContainSingle().Which.Should().Contain("already in flight");
            sent.Should().ContainSingle(m => m.Type == BifrostMessageType.Result,
                "the request that holds the id still answers");
        }

        [Fact]
        public async Task RequestPastTheConcurrencyCap_IsRefused()
        {
            var cap = BifrostBinaryMiddleware.MaxConcurrentRequestsPerConnection;
            var engine = new GatedEngine();
            var socket = new FakeWebSocket { Pipelined = true };
            for (uint id = 1; id <= cap + 1; id++)
                socket.EnqueueMessage(Query(id, "{ slow }"));
            // The slow requests stay held until the refusal is out, so all of them are in flight.
            socket.Sent += m =>
            {
                if (m.Type == BifrostMessageType.Error)
                    engine.Release.TrySetResult();
            };

            await RunAsync(engine, socket);

            var sent = socket.SentMessages();
            sent.Should().ContainSingle(m => m.Type == BifrostMessageType.Error)
                .Which.RequestId.Should().Be((uint)cap + 1);
            sent.Single(m => m.Type == BifrostMessageType.Error).Errors.Single().Should().Contain("Too many concurrent requests");
            sent.Count(m => m.Type == BifrostMessageType.Result).Should().Be(cap);
            engine.SlowEntered.Should().Be(cap);
        }
    }
}
