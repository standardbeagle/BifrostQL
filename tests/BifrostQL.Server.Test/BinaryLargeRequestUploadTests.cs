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
    /// A request bigger than one 4 MiB frame, sent the way @bifrostql/binary-client sends it:
    /// the encoded Query frame cut into 1 MiB Chunk frames, back to back. The server
    /// reassembles it and runs it as if it had arrived whole.
    /// </summary>
    public class BinaryLargeRequestUploadTests
    {
        /// <summary>The client's slice size (REQUEST_CHUNK_BYTES in the binary client).</summary>
        private const int ClientChunkBytes = 1024 * 1024;

        /// <summary>Answers with the length of the query text it was given.</summary>
        private sealed class QueryLengthEngine : IBifrostEngine
        {
            public Task<BifrostResult> ExecuteAsync(BifrostRequest request, string endpointPath)
                => Task.FromResult(new BifrostResult
                {
                    Data = new Dictionary<string, object?> { ["length"] = request.Query.Length },
                });
        }

        private sealed class SocketFeature : IHttpWebSocketFeature
        {
            private readonly WebSocket _socket;
            public SocketFeature(WebSocket socket) => _socket = socket;
            public bool IsWebSocketRequest => true;
            public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(_socket);
        }

        /// <summary>Slices an encoded request the way the binary client's sendRequestFrame does.</summary>
        private static IEnumerable<BifrostMessage> ClientChunks(uint requestId, byte[] frame, int chunkBytes = ClientChunkBytes)
        {
            var total = (uint)((frame.Length + chunkBytes - 1) / chunkBytes);
            for (uint i = 0; i < total; i++)
            {
                var offset = (int)i * chunkBytes;
                var slice = frame.AsSpan(offset, Math.Min(chunkBytes, frame.Length - offset)).ToArray();
                yield return new BifrostMessage
                {
                    RequestId = requestId,
                    Type = BifrostMessageType.Chunk,
                    Payload = slice,
                    ChunkSequence = i,
                    ChunkTotal = total,
                    ChunkOffset = (ulong)offset,
                    TotalBytes = (ulong)frame.Length,
                    ChunkChecksum = ChunkSender.ComputeCrc32(slice),
                };
            }
        }

        [Fact]
        public async Task FiveMebibyteRequest_SentAsClientChunks_RunsAndAnswers()
        {
            var query = "{ q(text: \"" + new string('x', 5 * 1024 * 1024) + "\") }";
            var frame = new BifrostMessage { RequestId = 11, Type = BifrostMessageType.Query, Query = query }.ToBytes();
            frame.Length.Should().BeGreaterThan(4 * 1024 * 1024, "the request must not fit one frame");

            var middleware = new BifrostBinaryMiddleware(
                _ => Task.CompletedTask,
                new QueryLengthEngine(),
                "/ws",
                NullLogger<BifrostBinaryMiddleware>.Instance,
                chunkThreshold: ChunkSender.DefaultChunkThreshold,
                ackWindow: ChunkSender.DefaultAckWindow,
                ackTimeout: ChunkSender.NoAckTimeout,
                requireAuthenticatedIdentity: false);
            var answered = new TaskCompletionSource<BifrostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var socket = new FakeWebSocket { HangWhenDrained = true };
            socket.Sent += m =>
            {
                if (m.Type is BifrostMessageType.Result or BifrostMessageType.Error)
                    answered.TrySetResult(m);
            };
            var chunks = ClientChunks(11, frame).ToList();
            foreach (var chunk in chunks)
                socket.EnqueueMessage(chunk);

            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
            context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
            var run = middleware.InvokeAsync(context);

            var answer = await answered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            answer.Type.Should().Be(BifrostMessageType.Result, string.Join("|", answer.Errors));
            answer.RequestId.Should().Be(11u);
            System.Text.Encoding.UTF8.GetString(answer.Payload).Should().Contain($"\"length\":{query.Length}");
            socket.SentMessages().Where(m => m.Type == BifrostMessageType.ChunkAck)
                .Select(m => m.ChunkSequence)
                .Should().Equal(Enumerable.Range(0, chunks.Count).Select(i => (uint)i),
                    "the server acks every upload chunk, which the client ignores");

            socket.DeliverClose();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        /// <summary>"{ hold }" runs until <see cref="Release"/> completes; anything else answers at once.</summary>
        private sealed class HoldingEngine : IBifrostEngine
        {
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<BifrostResult> ExecuteAsync(BifrostRequest request, string endpointPath)
            {
                if (request.Query.TrimEnd() == "{ hold }")
                    await Release.Task;
                return new BifrostResult { Data = new Dictionary<string, object?> { ["ok"] = true } };
            }
        }

        [Fact]
        public async Task ReassembledRequest_StaysChargedWhileItRuns_SoAnUploadOverTheConnectionCapIsRefused()
        {
            // The connection cap (1500 bytes here, 128 MiB by default) used to drop a request's
            // bytes the moment it was reassembled, though the server holds that request until it
            // ends: 16 concurrent 64 MiB uploads held about 1 GiB. Request 1 (~870 bytes) is
            // still running when request 2's first 700-byte chunk arrives, so 870 + 700 is over
            // the cap and the chunk is refused. Each size is under the 1000-byte session cap, so
            // only the connection cap can refuse it.
            const int sessionCap = 1000;
            const int connectionCap = 1500;
            var engine = new HoldingEngine();
            var middleware = new BifrostBinaryMiddleware(
                _ => Task.CompletedTask,
                engine,
                "/ws",
                NullLogger<BifrostBinaryMiddleware>.Instance,
                chunkThreshold: ChunkSender.DefaultChunkThreshold,
                ackWindow: ChunkSender.DefaultAckWindow,
                ackTimeout: ChunkSender.NoAckTimeout,
                requireAuthenticatedIdentity: false)
            {
                CreateChunkReceiver = () => new ChunkReceiver(
                    sessionCap, ChunkReceiver.DefaultMaxPendingReassemblies, TimeSpan.FromMinutes(5), connectionCap),
            };

            var held = new BifrostMessage { RequestId = 1, Type = BifrostMessageType.Query, Query = "{ hold }" + new string(' ', 850) }.ToBytes();
            var next = new BifrostMessage { RequestId = 2, Type = BifrostMessageType.Query, Query = "{ next }" + new string(' ', 850) }.ToBytes();
            held.Length.Should().BeInRange(connectionCap - 700 + 1, sessionCap, "request 1 plus 700 bytes must pass only the session cap");
            next.Length.Should().BeLessThanOrEqualTo(sessionCap);

            var secondAnswer = new TaskCompletionSource<BifrostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstResult = new TaskCompletionSource<BifrostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var socket = new FakeWebSocket { Pipelined = true, HangWhenDrained = true };
            socket.Sent += m =>
            {
                if (m.RequestId == 2 && m.Type is BifrostMessageType.Error or BifrostMessageType.ChunkAck)
                {
                    secondAnswer.TrySetResult(m);
                    engine.Release.TrySetResult(); // request 1 may end only once request 2's chunk is judged
                }
                if (m.RequestId == 1 && m.Type is BifrostMessageType.Result or BifrostMessageType.Error)
                    firstResult.TrySetResult(m);
            };
            foreach (var chunk in ClientChunks(1, held, 500))
                socket.EnqueueMessage(chunk);
            socket.EnqueueMessage(ClientChunks(2, next, 700).First());

            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
            context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
            var run = middleware.InvokeAsync(context);

            var answer = await secondAnswer.Task.WaitAsync(TimeSpan.FromSeconds(10));
            answer.Type.Should().Be(BifrostMessageType.Error,
                "request 1's reassembled bytes count against the connection cap until it ends");
            (await firstResult.Task.WaitAsync(TimeSpan.FromSeconds(10))).Type.Should().Be(BifrostMessageType.Result,
                "control: the held request itself runs and answers");

            socket.DeliverClose();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
