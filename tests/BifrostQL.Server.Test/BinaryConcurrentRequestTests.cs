using System.Collections.Concurrent;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
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

        /// <summary>
        /// The upgrade request's feature collection, which refuses every access once
        /// <paramref name="readerStarted"/> turns true. <see cref="HttpContext"/> is not thread-safe,
        /// and once the reader has taken a frame the connection's request tasks run concurrently: any
        /// read of the shared context from one of them can race another's lazy feature install. A
        /// refused access is recorded (the throw itself may be swallowed into an Error frame) and throws.
        /// </summary>
        private sealed class SealedOnServingFeatures : IFeatureCollection
        {
            private readonly FeatureCollection _inner = new();
            private readonly Func<bool> _readerStarted;
            public SealedOnServingFeatures(Func<bool> readerStarted) => _readerStarted = readerStarted;

            public ConcurrentQueue<string> AccessedWhileServing { get; } = new();
            public bool IsReadOnly => false;

            public int Revision
            {
                get
                {
                    Guard("Revision");
                    return _inner.Revision;
                }
            }

            public object? this[Type key]
            {
                get
                {
                    Guard(key.Name);
                    return _inner[key];
                }
                set
                {
                    Guard(key.Name);
                    _inner[key] = value;
                }
            }

            private void Guard(string what)
            {
                if (!_readerStarted())
                    return;
                AccessedWhileServing.Enqueue(what + " at " + Environment.StackTrace);
                throw new InvalidOperationException("A request task read the shared upgrade HttpContext.");
            }

            public TFeature? Get<TFeature>() => (TFeature?)this[typeof(TFeature)];
            public void Set<TFeature>(TFeature? instance) => this[typeof(TFeature)] = instance;
            public IEnumerator<KeyValuePair<Type, object>> GetEnumerator() => _inner.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>
        /// Gates like <see cref="GatedEngine"/>, and records what each request sees through
        /// <see cref="IHttpContextAccessor"/>: the engine's own route to the HttpContext.
        /// </summary>
        private sealed class AccessorProbeEngine : IBifrostEngine
        {
            private readonly GatedEngine _gate = new();
            public ConcurrentQueue<(HttpContext? Context, string? Header, object? Item, string? User, bool OwnServices)> Seen { get; } = new();

            public async Task<BifrostResult> ExecuteAsync(BifrostRequest request, string endpointPath)
            {
                var http = request.RequestServices!.GetRequiredService<IHttpContextAccessor>().HttpContext;
                Seen.Enqueue((http, http?.Request.Headers["X-Probe"].ToString(), http?.Items["probe"],
                    http?.User.Identity?.Name, http != null && ReferenceEquals(http.RequestServices, request.RequestServices)));
                return await _gate.ExecuteAsync(request, endpointPath);
            }
        }

        private static Task RunAsync(IBifrostEngine engine, FakeWebSocket socket, int ackWindow = 64)
        {
            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
            context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
            return RunAsync(engine, context, ackWindow);
        }

        private static Task RunAsync(IBifrostEngine engine, HttpContext context, int ackWindow = 64)
        {
            var middleware = new BifrostBinaryMiddleware(
                _ => Task.CompletedTask,
                engine,
                "/ws",
                NullLogger<BifrostBinaryMiddleware>.Instance,
                chunkThreshold: 100,
                ackWindow: ackWindow,
                ackTimeout: ChunkSender.NoAckTimeout,
                requireAuthenticatedIdentity: false);
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
            // serves one request at a time never answers either (RunAsync times out). Which
            // Result goes out first is a race: the fast request releases the slow one before
            // it sends its own answer, so the frame order proves nothing and is not asserted.
            var engine = new GatedEngine();
            var socket = new FakeWebSocket { Pipelined = true };
            socket.EnqueueMessage(Query(1, "{ slow }"));
            socket.EnqueueMessage(Query(2, "{ fast }"));

            await RunAsync(engine, socket);

            var sent = socket.SentMessages();
            sent.Should().NotContain(m => m.Type == BifrostMessageType.Error);
            sent.Should().ContainSingle(m => m.RequestId == 2 && m.Type == BifrostMessageType.Result);
            sent.Should().ContainSingle(m => m.RequestId == 1 && m.Type == BifrostMessageType.Result,
                "the slow request answers once the request pipelined behind it has run");
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
            var refusal = sent.Should().ContainSingle(m => m.Type == BifrostMessageType.Error).Which;
            refusal.ErrorCode.Should().Be(BinaryErrorCode.RequestIdInFlight,
                "a client tells this refusal from a failure by its code, never by its text");
            refusal.Errors.Should().ContainSingle().Which.Should().Contain("already in flight");
            sent.Should().ContainSingle(m => m.Type == BifrostMessageType.Result,
                "the request that holds the id still answers");
        }

        [Fact]
        public async Task ConcurrentRequests_NeverReadTheSharedHttpContext()
        {
            // The flake behind this fact: requests 5 and 6 ran at once on an anonymous mount, and
            // each one's session check was the first read of HttpContext.User. That getter installs
            // the authentication feature lazily, so both tasks wrote the context's feature
            // dictionary at once and one got a NullReferenceException. Every request-task read of
            // the shared context (the session check, the identity projection, and the engine's
            // IHttpContextAccessor route) is the same race, so once the reader is serving, the
            // upgrade request's features refuse any access at all.
            var socket = new FakeWebSocket { Pipelined = true };
            socket.EnqueueMessage(Query(1, "{ slow }"));
            socket.EnqueueMessage(Query(2, "{ fast }"));
            var features = new SealedOnServingFeatures(() => socket.ReceiveCount > 0);
            features.Set<IHttpRequestFeature>(new HttpRequestFeature());
            features.Set<IHttpResponseFeature>(new HttpResponseFeature());
            features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));
            features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
            var services = new ServiceCollection().AddHttpContextAccessor().BuildServiceProvider();
            var upgrade = new DefaultHttpContext(features)
            {
                RequestServices = services,
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "probe-user") },
                    "test")),
            };
            upgrade.Request.Headers["X-Probe"] = "probe-header";
            upgrade.Items["probe"] = "probe-item";
            // Hosting points the accessor at the upgrade request, as it does for every request.
            services.GetRequiredService<IHttpContextAccessor>().HttpContext = upgrade;
            var engine = new AccessorProbeEngine();

            await RunAsync(engine, upgrade);

            features.AccessedWhileServing.Should().BeEmpty(
                "a request task that reads the shared context races every other task on it");
            socket.SentMessages().Count(m => m.Type == BifrostMessageType.Result).Should().Be(2,
                "both requests ran, so both read their context");
            engine.Seen.Should().HaveCount(2).And.OnlyContain(seen =>
                seen.Context != null && !ReferenceEquals(seen.Context, upgrade)
                && seen.Header == "probe-header" && Equals(seen.Item, "probe-item") && seen.User == "probe-user"
                && seen.OwnServices,
                "each message reads a copy of the upgrade request, with its own services");
            engine.Seen.Select(seen => seen.Context).Distinct().Should().HaveCount(2,
                "two messages never share one context");
            services.GetRequiredService<IHttpContextAccessor>().HttpContext.Should().BeSameAs(upgrade,
                "a message points the accessor at its own context for itself only, never clearing the upgrade request's");
        }

        /// <summary>
        /// Records what a host's identity factory reads from each message's context: the route a
        /// mount like <c>/{tenant}/bifrost-ws</c> matched, the endpoint, the TLS handshake and the
        /// session. It then overwrites the tenant route value, which a later message must not see.
        /// </summary>
        private sealed class RouteProbeFactory : IBifrostAuthContextFactory
        {
            public ConcurrentQueue<(object? Tenant, string? Endpoint, System.Security.Authentication.SslProtocols? Tls,
                string? TlsHost, bool HasSession, bool SharesRouteValues)> Seen { get; } = new();
            public RouteValueDictionary? UpgradeRouteValues;

            public IDictionary<string, object?> CreateUserContext(HttpContext context)
            {
                var tls = context.Features.Get<Microsoft.AspNetCore.Connections.Features.ITlsHandshakeFeature>();
                Seen.Enqueue((context.GetRouteValue("tenant"), context.GetEndpoint()?.DisplayName, tls?.Protocol,
                    tls?.HostName, context.Features.Get<Microsoft.AspNetCore.Http.Features.ISessionFeature>() != null,
                    ReferenceEquals(context.Request.RouteValues, UpgradeRouteValues)));
                context.Request.RouteValues["tenant"] = "overwritten-by-a-message";
                return new Dictionary<string, object?> { ["user_id"] = "probe-user" };
            }
        }

#pragma warning disable SYSLIB0058 // the handshake's legacy algorithm members are obsolete in net10
        private sealed class TlsHandshake : Microsoft.AspNetCore.Connections.Features.ITlsHandshakeFeature
        {
            public System.Security.Authentication.SslProtocols Protocol { get; init; }
            public string HostName { get; init; } = "";
            public System.Security.Authentication.CipherAlgorithmType CipherAlgorithm => default;
            public int CipherStrength => 0;
            public System.Security.Authentication.HashAlgorithmType HashAlgorithm => default;
            public int HashStrength => 0;
            public System.Security.Authentication.ExchangeAlgorithmType KeyExchangeAlgorithm => default;
            public int KeyExchangeStrength => 0;
        }
#pragma warning restore SYSLIB0058

        [Fact]
        public async Task EachMessage_SeesTheUpgradeRouteEndpointAndTls_ButNoSession()
        {
            // A mount at /{tenant}/bifrost-ws whose identity factory reads RouteValues["tenant"]
            // got null inside every message: the message context carried no endpoint, no route
            // values and no TLS handshake. The session is deliberately absent (per request, not
            // thread-safe). The serial fake delivers message 2 only after message 1 answered, so
            // message 1's write to its route values has happened before message 2 reads its own.
            var socket = new FakeWebSocket();
            socket.EnqueueMessage(Query(1, "{ fast }"));
            socket.EnqueueMessage(Query(2, "{ fast }"));
            var factory = new RouteProbeFactory();
            var services = new ServiceCollection()
                .AddSingleton<IBifrostAuthContextFactory>(factory)
                .BuildServiceProvider();
            var upgrade = new DefaultHttpContext
            {
                RequestServices = services,
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "probe-user") },
                    "test")),
            };
            upgrade.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
            upgrade.SetEndpoint(new Endpoint(null, null, "tenant-mount"));
            upgrade.Request.RouteValues["tenant"] = "acme";
            factory.UpgradeRouteValues = upgrade.Request.RouteValues;
            upgrade.Features.Set<Microsoft.AspNetCore.Connections.Features.ITlsHandshakeFeature>(new TlsHandshake
            {
                Protocol = System.Security.Authentication.SslProtocols.Tls13,
                HostName = "acme.example",
            });
            upgrade.Features.Set<Microsoft.AspNetCore.Http.Features.ISessionFeature>(
                NSubstitute.Substitute.For<Microsoft.AspNetCore.Http.Features.ISessionFeature>());

            await RunAsync(new GatedEngine(), upgrade);

            socket.SentMessages().Count(m => m.Type == BifrostMessageType.Result).Should().Be(2);
            factory.Seen.Should().HaveCount(2).And.OnlyContain(seen =>
                Equals(seen.Tenant, "acme") && seen.Endpoint == "tenant-mount"
                && seen.Tls == System.Security.Authentication.SslProtocols.Tls13 && seen.TlsHost == "acme.example",
                "each message sees the upgrade request's route, endpoint and TLS handshake, unchanged by an earlier message");
            factory.Seen.Should().OnlyContain(seen => !seen.SharesRouteValues,
                "each message reads its own copy of the route values");
            factory.Seen.Should().OnlyContain(seen => !seen.HasSession,
                "the session is per request and not thread-safe, so no message gets it");
            upgrade.Request.RouteValues["tenant"].Should().Be("acme", "a message never writes the upgrade request's routes");
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
            var refusal = sent.Single(m => m.Type == BifrostMessageType.Error);
            refusal.ErrorCode.Should().Be(BinaryErrorCode.TooManyConcurrentRequests,
                "a client requeues this refusal by its code, never by its text");
            refusal.Errors.Single().Should().Contain("Too many concurrent requests");
            sent.Count(m => m.Type == BifrostMessageType.Result).Should().Be(cap);
            engine.SlowEntered.Should().Be(cap);
        }
    }
}
