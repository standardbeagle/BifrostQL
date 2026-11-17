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
        private static IEnumerable<BifrostMessage> ClientChunks(uint requestId, byte[] frame)
        {
            var total = (uint)((frame.Length + ClientChunkBytes - 1) / ClientChunkBytes);
            for (uint i = 0; i < total; i++)
            {
                var offset = (int)i * ClientChunkBytes;
                var slice = frame.AsSpan(offset, Math.Min(ClientChunkBytes, frame.Length - offset)).ToArray();
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
    }
}
