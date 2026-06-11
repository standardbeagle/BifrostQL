using System.Text;
using System.Text.Json;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// Verifies that exception details are never surfaced to GraphQL clients.
/// FIX 1: BifrostHttpMiddleware swallows exception text and returns a constant error.
/// FIX 2: BifrostBinaryMiddleware swallows exception text in execution-failure path.
/// </summary>
public sealed class HttpMiddlewareErrorScrubTests
{
    private const string SensitiveDetail = "Server=prod-db.internal;Password=super_secret;Database=bifrost";

    /// <summary>
    /// When the document executor throws, the client-facing error must be the constant
    /// "An unexpected server error occurred." — no exception text, no connection string.
    /// </summary>
    [Fact]
    public async Task ExecutorThrows_ClientSeesConstantMessage_NotExceptionText()
    {
        // Arrange: executor that throws with a message containing sensitive detail.
        var executor = Substitute.For<IDocumentExecuter>();
        executor
            .ExecuteAsync(Arg.Any<ExecutionOptions>())
            .Returns(Task.FromException<ExecutionResult>(new InvalidOperationException(SensitiveDetail)));

        var serializer = new GraphQLSerializer();
        var middleware = new BifrostHttpMiddleware(
            next: _ => Task.CompletedTask,
            serializer: serializer,
            documentExecutor: executor,
            logger: NullLogger<BifrostHttpMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        var body = JsonSerializer.Serialize(new { query = "{ __typename }" });
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(200);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(context.Response.Body).ReadToEndAsync();

        json.Should().Contain("An unexpected server error occurred.",
            because: "the constant safe message must reach the client");
        json.Should().NotContain(SensitiveDetail,
            because: "exception text must never be forwarded to clients");
        json.Should().NotContain("Server error:",
            because: "the old prefixed format must be gone");
    }

    /// <summary>
    /// Inner-exception message also must not leak.
    /// </summary>
    [Fact]
    public async Task ExecutorThrows_WithInnerException_InnerMessageDoesNotLeak()
    {
        var inner = new Exception("inner: " + SensitiveDetail);
        var outer = new InvalidOperationException("outer wrapper", inner);

        var executor = Substitute.For<IDocumentExecuter>();
        executor
            .ExecuteAsync(Arg.Any<ExecutionOptions>())
            .Returns(Task.FromException<ExecutionResult>(outer));

        var serializer = new GraphQLSerializer();
        var middleware = new BifrostHttpMiddleware(
            next: _ => Task.CompletedTask,
            serializer: serializer,
            documentExecutor: executor,
            logger: NullLogger<BifrostHttpMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        var body = JsonSerializer.Serialize(new { query = "{ __typename }" });
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(context.Response.Body).ReadToEndAsync();

        json.Should().Contain("An unexpected server error occurred.");
        json.Should().NotContain(SensitiveDetail);
        json.Should().NotContain("inner:");
        json.Should().NotContain("outer wrapper");
    }
}

/// <summary>
/// Verifies FIX 2: BifrostBinaryMiddleware execution-failure path returns "Query execution failed",
/// never the raw exception message.
/// </summary>
public sealed class BinaryMiddlewareErrorScrubTests
{
    private const string SensitiveDetail = "Server=prod-db.internal;Password=super_secret;Database=bifrost";

    /// <summary>
    /// When IBifrostEngine.ExecuteAsync throws, the response error text must be the generic
    /// "Query execution failed" string — no exception message, no connection-string detail.
    /// </summary>
    [Fact]
    public async Task EngineThrows_ClientFrameContainsGenericMessage_NotExceptionText()
    {
        // Arrange: engine that throws with a sensitive message.
        var engine = Substitute.For<IBifrostEngine>();
        engine
            .ExecuteAsync(Arg.Any<BifrostRequest>(), Arg.Any<string>())
            .Returns(Task.FromException<BifrostResult>(new InvalidOperationException(SensitiveDetail)));

        var fakeWs = new FakeWebSocket();

        // Queue a Query message then a Close so HandleConnectionAsync terminates.
        var queryMsg = new BifrostMessage
        {
            RequestId = 1,
            Type = BifrostMessageType.Query,
            Query = "{ users { id } }",
        };
        fakeWs.EnqueueMessage(queryMsg);
        fakeWs.EnqueueClose();

        var middleware = new BifrostBinaryMiddleware(
            next: _ => Task.CompletedTask,
            engine: engine,
            endpointPath: "/ws",
            logger: NullLogger<BifrostBinaryMiddleware>.Instance);

        var context = new DefaultHttpContext();
        // Install a fake IHttpWebSocketFeature so IsWebSocketRequest returns true
        // and AcceptWebSocketAsync returns our scriptable FakeWebSocket.
        context.Features.Set<IHttpWebSocketFeature>(new FakeWebSocketFeature(fakeWs));

        await middleware.InvokeAsync(context);

        // Assert: at least one response frame must be an Error with the generic message.
        var messages = fakeWs.SentMessages();
        var errorFrame = messages.FirstOrDefault(m => m.Type == BifrostMessageType.Error);
        errorFrame.Should().NotBeNull("engine failure must produce an Error frame");
        errorFrame!.Errors.Should().ContainSingle();
        errorFrame.Errors[0].Should().Be("Query execution failed",
            because: "the generic safe message must reach the client");
        errorFrame.Errors[0].Should().NotContain(SensitiveDetail,
            because: "exception text must never be forwarded to clients");
    }

    /// <summary>
    /// Minimal IHttpWebSocketFeature stub that returns the provided FakeWebSocket.
    /// </summary>
    private sealed class FakeWebSocketFeature : IHttpWebSocketFeature
    {
        private readonly FakeWebSocket _ws;
        public FakeWebSocketFeature(FakeWebSocket ws) => _ws = ws;
        public bool IsWebSocketRequest => true;
        public Task<System.Net.WebSockets.WebSocket> AcceptAsync(WebSocketAcceptContext context)
            => Task.FromResult<System.Net.WebSockets.WebSocket>(_ws);
    }
}
