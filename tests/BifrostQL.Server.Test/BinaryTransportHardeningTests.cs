using System.Net.WebSockets;
using System.Text.Json;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using GraphQL.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Security + correctness hardening tests for the binary WebSocket transport:
    /// CSWSH origin rejection (finding 1), IGraphQLSerializer payload shape (finding 2),
    /// and clean close on malformed reassembled frames (finding 3).
    /// </summary>
    public class BinaryTransportHardeningTests
    {
        private sealed class RecordingWebSocketFeature : IHttpWebSocketFeature
        {
            private readonly WebSocket _socket;
            public RecordingWebSocketFeature(WebSocket socket) => _socket = socket;
            public bool AcceptCalled { get; private set; }
            public bool IsWebSocketRequest => true;
            public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
            {
                AcceptCalled = true;
                return Task.FromResult(_socket);
            }
        }

        private sealed class DictEngine : IBifrostEngine
        {
            private readonly object? _data;
            public DictEngine(object? data) => _data = data;
            public Task<BifrostResult> ExecuteAsync(BifrostRequest request, string endpointPath)
                => Task.FromResult(new BifrostResult { Data = _data });
        }

        private static BifrostBinaryMiddleware Middleware(
            IBifrostEngine engine, string[]? allowedOrigins = null)
            => new(
                next: _ => Task.CompletedTask,
                engine: engine,
                endpointPath: "/ws",
                logger: NullLogger<BifrostBinaryMiddleware>.Instance,
                chunkThreshold: ChunkSender.DefaultChunkThreshold,
                ackWindow: ChunkSender.DefaultAckWindow,
                ackTimeout: ChunkSender.DefaultAckTimeout,
                allowedOrigins: allowedOrigins);

        // ---- Finding 1: CSWSH origin check ----

        [Fact]
        public async Task CrossOriginHandshake_IsRejected_WithoutUpgrade()
        {
            var socket = new FakeWebSocket();
            var feature = new RecordingWebSocketFeature(socket);
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpWebSocketFeature>(feature);
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("app.example.com");
            context.Request.Headers.Origin = "https://evil.attacker.test";

            await Middleware(new DictEngine(null)).InvokeAsync(context);

            feature.AcceptCalled.Should().BeFalse("a cross-origin handshake must not be upgraded");
            context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        }

        [Fact]
        public async Task SameOriginHandshake_IsAccepted()
        {
            var socket = new FakeWebSocket();
            socket.EnqueueClose();
            var feature = new RecordingWebSocketFeature(socket);
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpWebSocketFeature>(feature);
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("app.example.com");
            context.Request.Headers.Origin = "https://app.example.com";

            await Middleware(new DictEngine(null)).InvokeAsync(context);

            feature.AcceptCalled.Should().BeTrue("a same-origin handshake must be accepted");
        }

        [Fact]
        public async Task NoOriginHeader_IsAccepted_NonBrowserClient()
        {
            var socket = new FakeWebSocket();
            socket.EnqueueClose();
            var feature = new RecordingWebSocketFeature(socket);
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpWebSocketFeature>(feature);
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("app.example.com");
            // No Origin header (native client).

            await Middleware(new DictEngine(null)).InvokeAsync(context);

            feature.AcceptCalled.Should().BeTrue("a request with no Origin (non-browser) is not a CSRF vector");
        }

        [Fact]
        public async Task ConfiguredAllowlist_PermitsListedCrossOrigin_RejectsOthers()
        {
            var allow = new[] { "https://trusted.partner.test" };

            // Allowed cross-origin
            var okSocket = new FakeWebSocket();
            okSocket.EnqueueClose();
            var okFeature = new RecordingWebSocketFeature(okSocket);
            var okCtx = new DefaultHttpContext();
            okCtx.Features.Set<IHttpWebSocketFeature>(okFeature);
            okCtx.Request.Scheme = "https";
            okCtx.Request.Host = new HostString("app.example.com");
            okCtx.Request.Headers.Origin = "https://trusted.partner.test";
            await Middleware(new DictEngine(null), allow).InvokeAsync(okCtx);
            okFeature.AcceptCalled.Should().BeTrue("an allowlisted origin is accepted");

            // Rejected cross-origin not on the list
            var badSocket = new FakeWebSocket();
            var badFeature = new RecordingWebSocketFeature(badSocket);
            var badCtx = new DefaultHttpContext();
            badCtx.Features.Set<IHttpWebSocketFeature>(badFeature);
            badCtx.Request.Scheme = "https";
            badCtx.Request.Host = new HostString("app.example.com");
            badCtx.Request.Headers.Origin = "https://app.example.com"; // same-origin but allowlist is explicit
            await Middleware(new DictEngine(null), allow).InvokeAsync(badCtx);
            badFeature.AcceptCalled.Should().BeFalse("an explicit allowlist excludes anything not listed");
            badCtx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        }

        // ---- Finding 2: serialize via the registered IGraphQLSerializer ----

        [Fact]
        public async Task RealExecutionNodeData_SerializesToGraphQlShape_MatchingHttpPath()
        {
            // A REAL GraphQL execution produces an ExecutionNode graph as Data — not a plain
            // dictionary. Only the registered IGraphQLSerializer renders it into the correct
            // {"data":{...}} wire shape; a bare System.Text.Json pass emits node internals.
            var schema = Schema.For("type Query { hello: String }");
            var executer = new DocumentExecuter();
            var execResult = await executer.ExecuteAsync(o =>
            {
                o.Schema = schema;
                o.Query = "{ hello }";
            });
            execResult.Data.Should().NotBeNull();

            var serializer = new GraphQLSerializer();
            var services = new ServiceCollection();
            services.AddSingleton<IGraphQLSerializer>(serializer);
            await using var provider = services.BuildServiceProvider();

            // Expected HTTP-path bytes: the same serializer the GraphQLFrontend uses.
            var frontend = new GraphQLFrontend(serializer);
            using var httpStream = new MemoryStream();
            await frontend.SerializeAsync(
                httpStream, new BifrostResult { Data = execResult.Data }, CancellationToken.None);
            var httpJson = System.Text.Encoding.UTF8.GetString(httpStream.ToArray());

            // Drive the binary middleware end-to-end with the real ExecutionNode data.
            var socket = new FakeWebSocket();
            socket.EnqueueMessage(new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Query,
                Query = "{ hello }",
            });
            socket.EnqueueClose();

            var context = new DefaultHttpContext { RequestServices = provider };
            context.Features.Set<IHttpWebSocketFeature>(new PassthroughFeature(socket));

            await Middleware(new DictEngine(execResult.Data)).InvokeAsync(context);

            var resultFrame = socket.SentMessages().Single(m => m.Type == BifrostMessageType.Result);
            var payloadJson = System.Text.Encoding.UTF8.GetString(resultFrame.Payload);

            payloadJson.Should().Be(httpJson, "the binary payload must match the HTTP GraphQL wire shape");
            payloadJson.Should().Contain("\"data\"");
            payloadJson.Should().Contain("hello");

            // The old path — System.Text.Json directly on the ExecutionNode graph — cannot
            // even serialize it (throws), which is exactly the bug the serializer fix avoids.
            var stjAct = () => JsonSerializer.SerializeToUtf8Bytes(execResult.Data);
            stjAct.Should().Throw<Exception>(
                "serializing the raw ExecutionNode graph with System.Text.Json is the broken path");
        }

        // ---- Finding 3: malformed reassembled frame closes cleanly ----

        [Fact]
        public async Task GarbageReassembledFrame_ClosesConnectionCleanly_NoUnhandledException()
        {
            // A single client chunk whose (CRC-valid) assembled bytes are not a valid
            // BifrostMessage. FromBytes(assembled) throws; the connection must close cleanly
            // with an error frame, not tear down with an unhandled exception.
            var garbage = new byte[] { 0x1A, 0x05, 0x01 }; // declares a 5-byte string, supplies 1
            var chunk = new BifrostMessage
            {
                RequestId = 3,
                Type = BifrostMessageType.Chunk,
                Payload = garbage,
                ChunkSequence = 0,
                ChunkTotal = 1,
                ChunkOffset = 0,
                TotalBytes = (ulong)garbage.Length,
                ChunkChecksum = ChunkSender.ComputeCrc32(garbage),
            };

            var socket = new FakeWebSocket();
            socket.EnqueueMessage(chunk);

            var context = new DefaultHttpContext();
            context.Features.Set<IHttpWebSocketFeature>(new PassthroughFeature(socket));

            // Must complete without throwing.
            await Middleware(new DictEngine(null)).InvokeAsync(context);

            socket.State.Should().Be(WebSocketState.Closed, "a malformed frame must close the connection cleanly");
            socket.SentMessages().Should().Contain(m => m.Type == BifrostMessageType.Error,
                "the client should receive an error frame before the close");
        }

        private sealed class PassthroughFeature : IHttpWebSocketFeature
        {
            private readonly WebSocket _socket;
            public PassthroughFeature(WebSocket socket) => _socket = socket;
            public bool IsWebSocketRequest => true;
            public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(_socket);
        }
    }
}
