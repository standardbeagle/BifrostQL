using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// VERIFY-FIRST evidence for task 5.3: aggregate and dynamic-column PIVOT result sets
    /// are ordinary GraphQL queries whose results are serialized into the binary transport's
    /// opaque JSON <c>Payload</c> by the SAME <see cref="IGraphQLSerializer"/> the HTTP path
    /// (<see cref="GraphQLFrontend"/>) uses. There is no per-column protobuf shape at the
    /// envelope level (query text + variables JSON in, JSON payload out), so a dynamic pivot
    /// that expands one output column per distinct value rides the existing <c>Query</c> frame
    /// unchanged.
    ///
    /// This is proven by RUNNING each surface's query over the real binary WebSocket transport
    /// (the full <see cref="BifrostBinaryMiddleware"/> driven through a <see cref="FakeWebSocket"/>)
    /// AND over the HTTP serialization path, then asserting the two payloads are byte-identical —
    /// i.e. deep-equal at the wire, per surface (chart/dashboard aggregate, grouped-grid
    /// multi-column aggregate, and dynamic-column pivot). If either transport applied
    /// shape-specific handling, or if a dynamic pivot column were dropped/renamed on the binary
    /// path, these assertions would fail. They pass, so the finding is NO protocol change needed.
    /// </summary>
    public sealed class BinaryAggregatePivotPassthroughTests : IAsyncLifetime
    {
        private const string EndpointPath = "/bifrost-ws";

        private string _connectionString = null!;
        private SqliteConnection _keepAlive = null!;
        private SqliteDbConnFactory _connFactory = null!;
        private ProfileModelCache _profileCache = null!;

        public async Task InitializeAsync()
        {
            _connectionString = $"Data Source=bifrost_binary_aggpivot_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            _keepAlive = new SqliteConnection(_connectionString);
            await _keepAlive.OpenAsync();

            await Exec(
                @"CREATE TABLE orders (
                    id INTEGER PRIMARY KEY,
                    region TEXT NOT NULL,
                    status TEXT NOT NULL,
                    amount REAL NOT NULL
                );");
            // region x status crosstab with holes: (west, closed) is absent so the pivot
            // must emit a null cell, not a missing key — a real dynamic-column shape.
            await Exec(
                @"INSERT INTO orders(id, region, status, amount) VALUES
                    (1, 'east', 'open',    100),
                    (2, 'east', 'closed',  40),
                    (3, 'east', 'open',    10),
                    (4, 'west', 'open',    25),
                    (5, 'west', 'shipped', 55);");

            _connFactory = new SqliteDbConnFactory(_connectionString);
            var loader = new DbModelLoader(_connFactory, new MetadataLoader(Array.Empty<string>()));
            var read = await loader.ReadAsync();
            _profileCache = new ProfileModelCache(
                loader, read, Array.Empty<string>(), additionalMetadata: null, registry: null);
        }

        private async Task Exec(string sql)
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

        private ServiceProvider BuildProvider()
        {
            var filterTransformers = new FilterTransformersWrap
            {
                Transformers = Array.Empty<IFilterTransformer>(),
            };

            var pathCache = new PathCache<Inputs>();
            var (model, schema) = _profileCache.GetFor(null);
            pathCache.AddLoader(EndpointPath, () => Task.FromResult(new Inputs(new Dictionary<string, object?>
            {
                { "connFactory", _connFactory },
                { "model", model },
                { "dbSchema", schema },
                { "profileModelCache", _profileCache },
            })));

            var services = new ServiceCollection();
            services.AddSingleton<IFilterTransformers>(filterTransformers);
            services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
            {
                Transformers = Array.Empty<IMutationTransformer>(),
            });
            services.AddSingleton<IQueryTransformerService>(new QueryTransformerService(filterTransformers));
            services.AddSingleton<IQueryObservers>(new QueryObserversWrap());
            services.AddSingleton(pathCache);
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddSingleton<IDocumentExecuter>(new DocumentExecuter());
            // The binary payload serializer and the HTTP-path GraphQLFrontend must share this
            // exact registration, so both wires render result.Data identically.
            services.AddSingleton<IGraphQLSerializer>(new GraphQLSerializer());
            services.AddBifrostEngine();
            return services.BuildServiceProvider();
        }

        /// <summary>
        /// Runs <paramref name="query"/> over the real binary WebSocket transport and returns the
        /// decoded UTF-8 JSON of the single Result frame's payload — exactly what a binary client
        /// would <c>JSON.parse</c>.
        /// </summary>
        private async Task<string> BinaryPayloadJsonAsync(ServiceProvider provider, string query)
        {
            var socket = new FakeWebSocket();
            socket.EnqueueMessage(new BifrostMessage
            {
                RequestId = 7,
                Type = BifrostMessageType.Query,
                Query = query,
            });
            socket.EnqueueClose();

            var context = new DefaultHttpContext { RequestServices = provider };
            context.Features.Set<IHttpWebSocketFeature>(new AcceptFeature(socket));
            // ASP.NET Core populates IHttpContextAccessor per request (including WS upgrades);
            // the engine's profile resolution reads HttpContext through it, so set it here.
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

            var middleware = new BifrostBinaryMiddleware(
                next: _ => Task.CompletedTask,
                engine: provider.GetRequiredService<IBifrostEngine>(),
                endpointPath: EndpointPath,
                logger: NullLogger<BifrostBinaryMiddleware>.Instance,
                chunkThreshold: ChunkSender.DefaultChunkThreshold,
                ackWindow: ChunkSender.DefaultAckWindow,
                ackTimeout: ChunkSender.DefaultAckTimeout);

            await middleware.InvokeAsync(context);

            var result = socket.SentMessages().Single(m => m.Type == BifrostMessageType.Result);
            result.Errors.Should().BeEmpty("the query must succeed on the binary path");
            return Encoding.UTF8.GetString(result.Payload);
        }

        /// <summary>
        /// Runs <paramref name="query"/> through the engine and serializes it with the HTTP
        /// path's <see cref="GraphQLFrontend"/> — the reference wire bytes for the HTTP transport.
        /// </summary>
        private async Task<string> HttpPayloadJsonAsync(ServiceProvider provider, string query)
        {
            var context = new DefaultHttpContext { RequestServices = provider };
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

            var engine = provider.GetRequiredService<IBifrostEngine>();
            var result = await engine.ExecuteAsync(new BifrostRequest
            {
                Query = query,
                UserContext = new Dictionary<string, object?>(),
                RequestServices = provider,
                CancellationToken = default,
            }, EndpointPath);

            var frontend = new GraphQLFrontend(provider.GetRequiredService<IGraphQLSerializer>());
            using var ms = new System.IO.MemoryStream();
            await frontend.SerializeAsync(ms, result, CancellationToken.None);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public static IEnumerable<object[]> SurfaceQueries()
        {
            // Chart / dashboard: single-dimension aggregate.
            yield return new object[]
            {
                "chart-aggregate",
                "{ ordersAggregate(groupBy: [region]) { region _count _sum { amount } } }",
            };
            // Grouped grid: multi-column aggregate with several value ops.
            yield return new object[]
            {
                "grouped-grid-aggregate",
                "{ ordersAggregate(groupBy: [region, status]) { region status _count _sum { amount } _avg { amount } } }",
            };
            // Pivot: dynamic output columns (one per distinct status), returned as JSON.
            yield return new object[]
            {
                "pivot-dynamic-columns",
                "{ ordersPivot(rowKeys: [region], pivotColumn: status, valueColumn: amount, aggregate: sum) }",
            };
        }

        [Theory]
        [MemberData(nameof(SurfaceQueries))]
        public async Task Surface_BinaryPayload_IsByteIdenticalToHttp(string surface, string query)
        {
            await using var provider = BuildProvider();

            var binaryJson = await BinaryPayloadJsonAsync(provider, query);
            var httpJson = await HttpPayloadJsonAsync(provider, query);

            binaryJson.Should().Be(
                httpJson,
                $"the {surface} result must ride the existing Query frame unchanged — deep-equal to the HTTP wire");
            binaryJson.Should().Contain("\"data\"");
        }

        [Fact]
        public async Task Pivot_DynamicColumns_SurviveTheBinaryFrameUnchanged()
        {
            // The load-bearing case: a pivot expands one output column per distinct pivot value.
            // These column NAMES are data (status values), not schema — if the binary frame did
            // any per-column protobuf shaping they would be lost. Assert the actual dynamic
            // columns and a null hole are present in the binary payload.
            await using var provider = BuildProvider();

            var binaryJson = await BinaryPayloadJsonAsync(
                provider,
                "{ ordersPivot(rowKeys: [region], pivotColumn: status, valueColumn: amount, aggregate: sum) }");

            using var doc = JsonDocument.Parse(binaryJson);
            var pivot = doc.RootElement.GetProperty("data").GetProperty("ordersPivot");

            // Dynamic columns discovered from the data: closed, open, shipped (ordered).
            pivot.GetProperty("columns").EnumerateArray().Select(e => e.GetString())
                .Should().Equal("closed", "open", "shipped");

            var rows = pivot.GetProperty("rows").EnumerateArray()
                .ToDictionary(r => r.GetProperty("region").GetString()!, r => r.GetProperty("cells"));

            rows["east"].GetProperty("open").GetDouble().Should().Be(110);
            rows["east"].GetProperty("closed").GetDouble().Should().Be(40);
            // (west, closed) has no source row: a NULL cell, not an absent key.
            rows["west"].GetProperty("closed").ValueKind.Should().Be(JsonValueKind.Null);
            rows["west"].GetProperty("shipped").GetDouble().Should().Be(55);
        }

        private sealed class AcceptFeature : IHttpWebSocketFeature
        {
            private readonly WebSocket _socket;
            public AcceptFeature(WebSocket socket) => _socket = socket;
            public bool IsWebSocketRequest => true;
            public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(_socket);
        }
    }
}
