using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The binary transport gives each message its own DI scope, and the caller identity is
/// projected against that scope too. It used to project against the upgrade request's scope,
/// which lives for the whole socket, so a host's scoped grant resolver (and the DbContext it
/// reads) was shared by every concurrent message on the connection.
/// </summary>
public sealed class BinaryGrantResolverScopeTests
{
    private sealed class RecordingGrantResolver : IGrantResolver
    {
        public static readonly ConcurrentBag<RecordingGrantResolver> Used = new();

        public ValueTask<IReadOnlyCollection<string>> ResolveAsync(AppIdentity identity, CancellationToken ct)
        {
            Used.Add(this);
            return ValueTask.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());
        }
    }

    private sealed class EchoEngine : IBifrostEngine
    {
        public Task<BifrostResult> ExecuteAsync(BifrostRequest request, string endpointPath)
            => Task.FromResult(new BifrostResult { Data = new Dictionary<string, object?> { ["ok"] = true } });
    }

    private sealed class SocketFeature : IHttpWebSocketFeature
    {
        private readonly WebSocket _socket;
        public SocketFeature(WebSocket socket) => _socket = socket;
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(_socket);
    }

    [Fact]
    public async Task EachMessage_ProjectsItsIdentityWithItsOwnScopedGrantResolver()
    {
        var services = new ServiceCollection();
        services.AddScoped<IGrantResolver, RecordingGrantResolver>();
        await using var root = services.BuildServiceProvider();
        await using var upgradeScope = root.CreateAsyncScope();

        var socket = new FakeWebSocket { Pipelined = true };
        socket.EnqueueMessage(new BifrostMessage { RequestId = 1, Type = BifrostMessageType.Query, Query = "{ a }" });
        socket.EnqueueMessage(new BifrostMessage { RequestId = 2, Type = BifrostMessageType.Query, Query = "{ b }" });

        var middleware = new BifrostBinaryMiddleware(
            _ => Task.CompletedTask,
            new EchoEngine(),
            "/ws",
            NullLogger<BifrostBinaryMiddleware>.Instance,
            chunkThreshold: 1 << 20,
            ackWindow: 64,
            ackTimeout: ChunkSender.NoAckTimeout,
            requireAuthenticatedIdentity: true);
        var context = new DefaultHttpContext { RequestServices = upgradeScope.ServiceProvider };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(BifrostQL.Server.Auth.LocalAuthClaims.Provider, "local"),
        }, authenticationType: "Test"));
        context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));

        RecordingGrantResolver.Used.Clear();
        await middleware.InvokeAsync(context).WaitAsync(TimeSpan.FromSeconds(10));

        socket.SentMessages().Where(m => m.Type == BifrostMessageType.Result).Should().HaveCount(2);
        var upgradeResolver = (RecordingGrantResolver)upgradeScope.ServiceProvider.GetRequiredService<IGrantResolver>();
        // The upgrade gate projects once against the upgrade request's own scope, before the
        // message loop starts; that one use is legitimate. Every later use is a message's.
        RecordingGrantResolver.Used.Count(r => r == upgradeResolver).Should().Be(1,
            "only the upgrade-time gate uses the socket-lived scope; no message does");
        var perMessage = RecordingGrantResolver.Used.Where(r => r != upgradeResolver).ToList();
        perMessage.Should().HaveCount(2, "each message projects its identity once");
        perMessage.Distinct().Should().HaveCount(2, "each message resolves grants through its own scope");
    }
}
