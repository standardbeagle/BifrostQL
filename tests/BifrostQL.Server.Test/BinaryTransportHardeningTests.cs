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
            IBifrostEngine engine,
            string[]? allowedOrigins = null,
            int maxConnections = BifrostBinaryMiddleware.DefaultMaxConnections)
            => new(
                next: _ => Task.CompletedTask,
                engine: engine,
                endpointPath: "/ws",
                logger: NullLogger<BifrostBinaryMiddleware>.Instance,
                chunkThreshold: ChunkSender.DefaultChunkThreshold,
                ackWindow: ChunkSender.DefaultAckWindow,
                ackTimeout: ChunkSender.DefaultAckTimeout,
                requireAuthenticatedIdentity: false,
                allowedOrigins: allowedOrigins,
                maxConnections: maxConnections);

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

        // ---- H8: admission cap, pre-auth deadline, and the mount's identity gate ----

        [Fact]
        public async Task ConnectionCap_IsTakenAtUpgrade_AndRefusesTheNextUpgrade()
        {
            // The slot must be reserved at the upgrade, BEFORE the identity gate and before a
            // single frame is read: an unauthenticated peer's connection already costs a 4 MB
            // receive buffer and a reassembly budget. Revert-proof: constructing the middleware
            // with maxConnections: 2 (or removing the TryAcquire) admits the second upgrade and
            // fails the 503 assertion below.
            var middleware = Middleware(new DictEngine(null), maxConnections: 1);

            // First connection: hangs on receive, so it holds its slot. Everything up to that
            // receive runs synchronously, so the slot is held by the time InvokeAsync returns.
            var held = new FakeWebSocket { HangWhenDrained = true };
            using var abort = new CancellationTokenSource();
            var heldContext = new DefaultHttpContext { RequestAborted = abort.Token };
            heldContext.Features.Set<IHttpWebSocketFeature>(new PassthroughFeature(held));
            var heldConnection = middleware.InvokeAsync(heldContext);

            middleware.ActiveConnections.Should().Be(1, "the first upgrade took the only slot");

            var refusedFeature = new RecordingWebSocketFeature(new FakeWebSocket());
            var refusedContext = new DefaultHttpContext();
            refusedContext.Features.Set<IHttpWebSocketFeature>(refusedFeature);

            await middleware.InvokeAsync(refusedContext);

            refusedFeature.AcceptCalled.Should().BeFalse(
                "an upgrade over the cap must be refused before it is accepted");
            refusedContext.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

            abort.Cancel();
            await heldConnection;
            middleware.ActiveConnections.Should().Be(0, "the slot is released with the connection");
        }

        [Fact]
        public async Task AdmittedConnection_ThatNeverSendsAFrame_IsClosedOnTheDeadline()
        {
            // Pre-auth deadline: the slot is held from the upgrade, so a silent peer must not
            // keep it for free.
            var socket = new FakeWebSocket { HangWhenDrained = true };
            var middleware = new BifrostBinaryMiddleware(
                next: _ => Task.CompletedTask,
                engine: new DictEngine(null),
                endpointPath: "/ws",
                logger: NullLogger<BifrostBinaryMiddleware>.Instance,
                chunkThreshold: ChunkSender.DefaultChunkThreshold,
                ackWindow: ChunkSender.DefaultAckWindow,
                ackTimeout: ChunkSender.DefaultAckTimeout,
                requireAuthenticatedIdentity: false,
                firstFrameTimeout: TimeSpan.FromMilliseconds(100));

            var context = new DefaultHttpContext();
            context.Features.Set<IHttpWebSocketFeature>(new PassthroughFeature(socket));

            await middleware.InvokeAsync(context);

            socket.ClosedWith.Should().Be(WebSocketCloseStatus.PolicyViolation,
                "a peer that says nothing must not hold its admission slot indefinitely");
            middleware.ActiveConnections.Should().Be(0);
        }

        [Fact]
        public async Task AuthRequiredMount_AnonymousUpgrade_IsClosedBeforeAnyFrameIsRead()
        {
            // The middleware-level counterpart of the end-to-end gate fact in
            // BinaryProfileAuthorizationTests: nothing is read and nothing is executed.
            var socket = new FakeWebSocket();
            socket.EnqueueMessage(new BifrostMessage
            {
                RequestId = 1,
                Type = BifrostMessageType.Query,
                Query = "{ __typename }",
            });
            var engine = new CountingEngine();
            var middleware = new BifrostBinaryMiddleware(
                next: _ => Task.CompletedTask,
                engine: engine,
                endpointPath: "/ws",
                logger: NullLogger<BifrostBinaryMiddleware>.Instance,
                chunkThreshold: ChunkSender.DefaultChunkThreshold,
                ackWindow: ChunkSender.DefaultAckWindow,
                ackTimeout: ChunkSender.DefaultAckTimeout,
                requireAuthenticatedIdentity: true);

            var context = new DefaultHttpContext();
            context.Features.Set<IHttpWebSocketFeature>(new PassthroughFeature(socket));

            await middleware.InvokeAsync(context);

            socket.ClosedWith.Should().Be(WebSocketCloseStatus.PolicyViolation);
            socket.ReceiveCount.Should().Be(0, "the queued query frame must never be read");
            engine.Executions.Should().Be(0, "no query reaches the engine on an anonymous socket");
        }

        private sealed class CountingEngine : IBifrostEngine
        {
            public int Executions { get; private set; }
            public Task<BifrostResult> ExecuteAsync(BifrostRequest request, string endpointPath)
            {
                Executions++;
                return Task.FromResult(new BifrostResult { Data = null });
            }
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
