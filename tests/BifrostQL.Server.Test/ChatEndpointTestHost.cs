using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Chat;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Shared fixture for the chat-endpoint suites: an isolated in-memory SQLite
    /// chat pair (tenant-filtered, history-enabled), a TestServer host wired through
    /// <c>AddBifrostEndpoints</c> + <c>UseBifrostChat</c>, header-driven test
    /// authentication, and a scripted <see cref="IChatCompletionService"/> fake so
    /// no test touches the network.
    /// </summary>
    internal sealed class ChatEndpointHost : IAsyncDisposable
    {
        private readonly string _connString =
            $"Data Source=chat_endpoint_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private SqliteConnection _keepAlive = null!;
        private IHost? _host;

        public FakeChatCompletionService Fake { get; } = new();

        public async Task InitializeAsync()
        {
            _keepAlive = new SqliteConnection(_connString);
            await _keepAlive.OpenAsync();
            await ExecAsync(
                """
                CREATE TABLE conversations (
                    id        INTEGER PRIMARY KEY,
                    tenant_id TEXT NOT NULL,
                    title     TEXT NULL
                )
                """);
            await ExecAsync(
                """
                CREATE TABLE messages (
                    id              INTEGER PRIMARY KEY,
                    tenant_id       TEXT NOT NULL,
                    conversation_id INTEGER NOT NULL REFERENCES conversations(id),
                    role            TEXT NOT NULL,
                    content         TEXT NULL,
                    created_at      DATETIME NULL
                )
                """);
            await ExecAsync(
                """
                CREATE TABLE __history (
                    id              INTEGER PRIMARY KEY,
                    entity          TEXT NOT NULL,
                    entity_id       TEXT NOT NULL,
                    op              TEXT NOT NULL,
                    actor           TEXT NULL,
                    changed_at      TEXT NOT NULL,
                    before          TEXT NULL,
                    after           TEXT NULL,
                    changed_columns TEXT NULL,
                    tenant_id       TEXT NULL
                )
                """);
        }

        public async ValueTask DisposeAsync()
        {
            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
            await _keepAlive.DisposeAsync();
        }

        public async Task<HttpClient> StartAsync(
            int historyLimit = 50, string? systemPrompt = null, IQueryObserver[]? observers = null,
            IChatConnector[]? connectors = null, bool messagesAsExploreConnector = false)
        {
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IChatCompletionService>(Fake);
                    foreach (var connector in connectors ?? Array.Empty<IChatConnector>())
                        services.AddSingleton(connector);
                    services.AddAuthentication(HeaderAuthHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(HeaderAuthHandler.SchemeName, _ => { });
                    services.AddBifrostEndpoints(o =>
                    {
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = _connString;
                            e.Provider = "sqlite";
                            e.Path = "/graphql";
                            e.Metadata = new[]
                            {
                                "*.conversations { chat-conversations: enabled; chat-title: title; tenant-filter: tenant_id }",
                                "*.messages { chat-messages: enabled; chat-role: role; chat-content: content; " +
                                    "chat-conversation-fk: conversation_id; chat-created-at: created_at; " +
                                    "tenant-filter: tenant_id; history: enabled" +
                                    (messagesAsExploreConnector ? "; chat-connector: explore" : "") + " }",
                                ":root { history-table: main.__history }",
                            };
                            e.DisableAuth = true;
                        });
                        if (observers is not null)
                            o.AddQueryObservers(observers);
                    });
                });
                web.Configure(app =>
                {
                    app.UseAuthentication();
                    app.UseBifrostChat(o =>
                    {
                        o.HistoryLimit = historyLimit;
                        o.SystemPrompt = systemPrompt;
                    });
                    app.UseBifrostEndpoints();
                });
            });
            _host = await builder.StartAsync();
            return _host.GetTestClient();
        }

        public static HttpRequestMessage Post(string path, object? body, string? user, string? tenant)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path);
            if (body is not null)
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (user is not null)
                request.Headers.Add(HeaderAuthHandler.UserHeader, user);
            if (tenant is not null)
                request.Headers.Add(HeaderAuthHandler.TenantHeader, tenant);
            return request;
        }

        public async Task<long> CreateConversationAsync(HttpClient client, string tenant, string? title = null)
        {
            using var response = await client.SendAsync(Post(
                "/_chat/conversations", title is null ? new { } : new { title }, $"user-of-{tenant}", tenant));
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("id").GetInt64();
        }

        public Task<HttpResponseMessage> PostMessageAsync(
            HttpClient client, long conversationId, string content, string tenant = "tenant-a",
            HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
            CancellationToken cancellationToken = default) =>
            client.SendAsync(
                Post($"/_chat/conversations/{conversationId}/messages", new { content }, $"user-of-{tenant}", tenant),
                completion, cancellationToken);

        public async Task ExecAsync(string sql)
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            var value = await cmd.ExecuteScalarAsync();
            return value == DBNull.Value ? null : value;
        }
    }

    /// <summary>
    /// Header-driven test authentication: <c>X-Test-User</c> authenticates the
    /// request, <c>X-Test-Tenant</c> adds the tenant claim the tenant transformers
    /// consume. No headers = unauthenticated, exactly like an anonymous caller.
    /// </summary>
    internal sealed class HeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";
        public const string UserHeader = "X-Test-User";
        public const string TenantHeader = "X-Test-Tenant";

        public HeaderAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers[UserHeader].ToString();
            if (string.IsNullOrEmpty(user))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user) };
            var tenant = Request.Headers[TenantHeader].ToString();
            if (!string.IsNullOrEmpty(tenant))
                claims.Add(new Claim(BifrostQL.Server.Auth.LocalAuthClaims.Tenant, tenant));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }

    /// <summary>
    /// Scripted <see cref="IChatCompletionService"/>: no network, deterministic
    /// deltas/terminals per test, and it records every history it was called with.
    /// </summary>
    internal sealed class FakeChatCompletionService : IChatCompletionService
    {
        public Func<IReadOnlyList<ChatCompletionMessage>, CancellationToken, IAsyncEnumerable<ChatCompletionEvent>> Script
        { get; set; } = Scripts.Deltas(new[] { "ok" }, new ChatCompletionResult("ok", ChatCompletionStopReason.Complete, null, 1, 1));

        public List<IReadOnlyList<ChatCompletionMessage>> Calls { get; } = new();

        /// <summary>The request options of every call, in order — the tool-options pinning seam.</summary>
        public List<ChatCompletionRequestOptions?> OptionsCalls { get; } = new();

        public IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            IReadOnlyList<ChatCompletionMessage> history,
            ChatCompletionRequestOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add(history);
                OptionsCalls.Add(options);
            }
            return Script(history, cancellationToken);
        }
    }

    internal static class Scripts
    {
        public static Func<IReadOnlyList<ChatCompletionMessage>, CancellationToken, IAsyncEnumerable<ChatCompletionEvent>>
            Deltas(string[] deltas, ChatCompletionResult terminal) =>
            (_, ct) => Stream(deltas, terminal, ct);

        private static async IAsyncEnumerable<ChatCompletionEvent> Stream(
            string[] deltas, ChatCompletionResult terminal, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var delta in deltas)
            {
                ct.ThrowIfCancellationRequested();
                yield return new ChatCompletionDelta(delta);
            }
            await Task.Yield();
            yield return terminal;
        }

        public static async IAsyncEnumerable<ChatCompletionEvent> DeltaThenThrow(
            string delta, Exception exception, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new ChatCompletionDelta(delta);
            await Task.Yield();
            throw exception;
        }

        /// <summary>Streams one delta, then hangs until cancelled; signals the observed cancellation.</summary>
        public static async IAsyncEnumerable<ChatCompletionEvent> DeltaThenHang(
            string delta, TaskCompletionSource cancelled, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new ChatCompletionDelta(delta);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        }

        /// <summary>Streams one delta, signals <paramref name="started"/>, then waits for <paramref name="gate"/>.</summary>
        public static async IAsyncEnumerable<ChatCompletionEvent> DeltaThenGate(
            string delta, TaskCompletionSource started, TaskCompletionSource gate,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new ChatCompletionDelta(delta);
            started.TrySetResult();
            await gate.Task.WaitAsync(ct);
            yield return new ChatCompletionResult(delta, ChatCompletionStopReason.Complete, null, 1, 1);
        }
    }

    internal sealed record SseEvent(string Name, JsonElement Data);

    /// <summary>Minimal SSE decoder for the tests: named events with JSON data lines.</summary>
    internal static class SseReader
    {
        public static List<SseEvent> Parse(string body)
        {
            var events = new List<SseEvent>();
            string? name = null;
            var data = new StringBuilder();
            foreach (var rawLine in body.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                    name = line["event: ".Length..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                    data.Append(line["data: ".Length..]);
                else if (line.Length == 0 && name is not null)
                {
                    events.Add(Create(name, data.ToString()));
                    name = null;
                    data.Clear();
                }
            }
            return events;
        }

        public static async Task<SseEvent> ReadNextAsync(StreamReader reader)
        {
            string? name = null;
            var data = new StringBuilder();
            while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) is { } line)
            {
                line = line.TrimEnd('\r');
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                    name = line["event: ".Length..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                    data.Append(line["data: ".Length..]);
                else if (line.Length == 0 && name is not null)
                    return Create(name, data.ToString());
            }
            throw new InvalidOperationException("The SSE stream ended before a complete event arrived.");
        }

        private static SseEvent Create(string name, string data)
        {
            using var doc = JsonDocument.Parse(data);
            return new SseEvent(name, doc.RootElement.Clone());
        }
    }
}
