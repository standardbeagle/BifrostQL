using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Chat;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Connector slice 4 at the HTTP seam: <c>GET {Path}/media/{table}/{id}</c>
    /// resolves binary-mode <c>bifrost-media://</c> references — fail-closed
    /// identity gate first, then a re-authorizing intent-executor read under the
    /// CALLER's context, so cross-tenant and nonexistent rows are the same 404 and
    /// the reference itself needs no signature. URL-mode tables 404 (clients use
    /// the stored URL directly), and the content type is sniffed from the bytes.
    /// Also pinned: the middleware relays <see cref="ChatToolMediaActivity"/> as
    /// SSE <c>media</c> events between the tool result and the answer.
    /// </summary>
    public class ChatMediaEndpointTests : IAsyncLifetime
    {
        private static readonly byte[] PngBytes =
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }
                .Concat(Enumerable.Repeat((byte)0x24, 16)).ToArray();

        private static readonly string[] MediaMetadata =
        {
            "*.documents { chat-connector: media; chat-media-column: content; " +
                "chat-media-caption: caption; tenant-filter: tenant_id }",
            "*.links { chat-connector: media; chat-media-column: url; tenant-filter: tenant_id }",
        };

        private readonly ChatEndpointHost _h = new();

        public async Task InitializeAsync()
        {
            await _h.InitializeAsync();
            await _h.ExecAsync(
                """
                CREATE TABLE documents (
                    id        INTEGER PRIMARY KEY,
                    tenant_id TEXT NOT NULL,
                    caption   TEXT NULL,
                    content   BLOB NULL
                )
                """);
            await _h.ExecAsync(
                """
                CREATE TABLE links (
                    id        INTEGER PRIMARY KEY,
                    tenant_id TEXT NOT NULL,
                    url       TEXT NULL
                )
                """);
            await _h.ExecAsync(
                "INSERT INTO documents (tenant_id, caption, content) VALUES ('tenant-a', 'contract', @content)",
                ("@content", PngBytes));
            await _h.ExecAsync(
                "INSERT INTO documents (tenant_id, caption, content) VALUES ('tenant-a', 'plain bytes', @content)",
                ("@content", new byte[] { 0x01, 0x02, 0x03, 0x04 }));
            await _h.ExecAsync(
                "INSERT INTO links (tenant_id, url) VALUES ('tenant-a', 'https://cdn.example.com/logo.png')");
        }

        public async Task DisposeAsync() => await _h.DisposeAsync();

        private Task<HttpClient> StartAsync() => _h.StartAsync(extraMetadata: MediaMetadata);

        private static HttpRequestMessage BuildGet(string path, string? tenant)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (tenant is not null)
            {
                request.Headers.Add(HeaderAuthHandler.UserHeader, $"user-of-{tenant}");
                request.Headers.Add(HeaderAuthHandler.TenantHeader, tenant);
            }
            return request;
        }

        [Fact]
        public async Task Get_media_unauthenticated_is_401_before_anything_else()
        {
            var client = await StartAsync();

            using var response = await client.SendAsync(BuildGet("/_chat/media/documents/1", tenant: null));

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Get_media_happy_path_streams_the_bytes_with_the_sniffed_content_type()
        {
            var client = await StartAsync();

            using var response = await client.SendAsync(BuildGet("/_chat/media/documents/1", "tenant-a"));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(PngBytes);
        }

        [Fact]
        public async Task Get_media_over_the_size_cap_is_413()
        {
            // The route used to serve any stored size, decoding the whole value per fetch.
            var capped = await _h.StartAsync(extraMetadata: MediaMetadata,
                configureChat: o => o.MaxMediaBytes = PngBytes.Length - 1);
            using (var over = await capped.SendAsync(BuildGet("/_chat/media/documents/1", "tenant-a")))
            {
                over.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
                (await over.Content.ReadAsStringAsync()).Should().Contain("media-too-large");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Get_media_size_is_measured_in_the_database_before_the_value_is_read(bool fits)
        {
            // The route used to read the whole value (as base64) and only then compare it with
            // the cap. Now one statement measures LENGTH(content) and the value is selected only
            // when it fits: an oversized value runs exactly that one statement.
            var sql = new DocumentsSqlCapture();
            var client = await _h.StartAsync(extraMetadata: MediaMetadata, observers: new IQueryObserver[] { sql },
                configureChat: o => o.MaxMediaBytes = fits ? PngBytes.Length : PngBytes.Length - 1);

            using var response = await client.SendAsync(BuildGet("/_chat/media/documents/1", "tenant-a"));

            response.StatusCode.Should().Be(fits ? HttpStatusCode.OK : HttpStatusCode.RequestEntityTooLarge);
            sql.Statements.Should().HaveCount(fits ? 2 : 1,
                fits ? "control: a value that fits is read after the probe" : "an oversized value is never selected");
            sql.Statements[0].Should().Contain("LENGTH(", "the first statement measures the stored size");
        }

        private sealed class DocumentsSqlCapture : IQueryObserver
        {
            private readonly object _gate = new();
            private readonly List<string> _statements = new();
            public QueryPhase[] Phases { get; } = { QueryPhase.AfterExecute };
            public IReadOnlyList<string> Statements { get { lock (_gate) return _statements.ToArray(); } }
            public ValueTask OnQueryPhaseAsync(QueryPhase phase, QueryObserverContext context)
            {
                if (string.Equals(context.Table.DbName, "documents", StringComparison.OrdinalIgnoreCase))
                    lock (_gate) _statements.Add(context.Sql ?? string.Empty);
                return ValueTask.CompletedTask;
            }
        }

        [Fact]
        public async Task Get_media_exactly_at_the_size_cap_is_served()
        {
            var client = await _h.StartAsync(extraMetadata: MediaMetadata,
                configureChat: o => o.MaxMediaBytes = PngBytes.Length);

            using var response = await client.SendAsync(BuildGet("/_chat/media/documents/1", "tenant-a"));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(PngBytes);
        }

        [Fact]
        public async Task Get_media_response_carries_nosniff_and_content_disposition()
        {
            // Stored bytes are attacker-controlled content served from this origin:
            // like the blob endpoint, the media route must pin the sniffed type
            // (nosniff) and declare a disposition so stored markup can never
            // execute inline. Octet-stream falls back to an attachment.
            var client = await StartAsync();

            using var image = await client.SendAsync(BuildGet("/_chat/media/documents/1", "tenant-a"));
            using var unknown = await client.SendAsync(BuildGet("/_chat/media/documents/2", "tenant-a"));

            image.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
            image.Content.Headers.ContentDisposition!.DispositionType.Should().Be("inline");
            unknown.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
            unknown.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        }

        [Fact]
        public async Task Get_media_unrecognized_bytes_fall_back_to_octet_stream()
        {
            var client = await StartAsync();

            using var response = await client.SendAsync(BuildGet("/_chat/media/documents/2", "tenant-a"));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        }

        [Fact]
        public async Task Get_media_cross_tenant_and_nonexistent_are_the_same_404()
        {
            // tenant-b cannot see tenant-a's row 1; row 999 does not exist. The
            // responses must be indistinguishable — fail-closed both directions.
            var client = await StartAsync();

            using var crossTenant = await client.SendAsync(BuildGet("/_chat/media/documents/1", "tenant-b"));
            using var missing = await client.SendAsync(BuildGet("/_chat/media/documents/999", "tenant-b"));

            crossTenant.StatusCode.Should().Be(HttpStatusCode.NotFound);
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await crossTenant.Content.ReadAsStringAsync())
                .Should().Be(await missing.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Get_media_url_mode_table_unknown_table_and_malformed_id_are_404()
        {
            var client = await StartAsync();

            // URL mode: the client uses the stored URL directly; the endpoint serves
            // nothing (and must not turn into a proxy for arbitrary stored URLs).
            (await client.SendAsync(BuildGet("/_chat/media/links/1", "tenant-a")))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            // No such connector table.
            (await client.SendAsync(BuildGet("/_chat/media/nope/1", "tenant-a")))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            // The messages table exists but is not a media connector.
            (await client.SendAsync(BuildGet("/_chat/media/messages/1", "tenant-a")))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            // Malformed id for an integer key.
            (await client.SendAsync(BuildGet("/_chat/media/documents/abc", "tenant-a")))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Post_to_the_media_route_is_405_with_the_get_allow_header()
        {
            var client = await StartAsync();

            using var response = await client.SendAsync(
                ChatEndpointHost.Post("/_chat/media/documents/1", new { }, "user-of-tenant-a", "tenant-a"));

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            response.Content.Headers.Allow.Should().BeEquivalentTo("GET");
        }

        // ---- SSE relay of media references --------------------------------------------

        private static async IAsyncEnumerable<ChatCompletionEvent> Events(
            ChatCompletionEvent[] events, [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var evt in events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return evt;
            }
        }

        [Fact]
        public async Task Sse_relays_media_events_after_the_tool_result_phase()
        {
            // Arrange: a media-bearing tool round-trip; the middleware must relay the
            // references as one `media` event, in stream order, with null fields
            // omitted from the items.
            var client = await StartAsync();
            _h.Fake.Script = (_, ct) => Events(new ChatCompletionEvent[]
            {
                new ChatToolActivity("media_documents", ChatToolPhase.Call, "{}"),
                new ChatToolActivity("media_documents", ChatToolPhase.Result, """{"rows":[...]}"""),
                new ChatToolMediaActivity("media_documents", new[]
                {
                    new ChatToolMediaReference(
                        "documents", "content", 1L, null, "bifrost-media://documents/1", "contract"),
                }),
                new ChatCompletionDelta("Here is the contract."),
                new ChatCompletionResult("Here is the contract.", ChatCompletionStopReason.Complete, null, 1, 1),
            }, ct);
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");

            // Act
            using var response = await _h.PostMessageAsync(client, conversationId, "Show me the contract");
            var events = SseReader.Parse(await response.Content.ReadAsStringAsync());

            // Assert
            events.Select(e => e.Name).Should().Equal(
                "message-accepted", "tool", "tool", "media", "delta", "done");
            var media = events[3].Data;
            media.GetProperty("toolName").GetString().Should().Be("media_documents");
            var item = media.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
            item.GetProperty("id").GetInt64().Should().Be(1);
            item.GetProperty("mediaReference").GetString().Should().Be("bifrost-media://documents/1");
            item.GetProperty("caption").GetString().Should().Be("contract");
            item.TryGetProperty("contentType", out _).Should().BeFalse("null fields are omitted");
        }
    }
}
