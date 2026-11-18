using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Chat;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// A local-auth session ended while a /_chat completion streams stops the stream at the next
    /// tool call: the cookie hook ran once, at the request's start, and the stream outlives it.
    /// </summary>
    public sealed class ChatLocalSessionTests
    {
        private const string Login = "alice@club.test";
        private const string Password = "correct horse battery";

        private static readonly PasswordHasher<string> CheapHasher = new(
            Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));


        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ChatStream_RunsNoToolAfterTheLocalSessionEnds_AndEndsWithSessionEnded(bool revokeMidStream)
        {
            // The /_chat stream is one long HTTP request: the cookie hook ran once, at its start. A
            // session ended while the model works must stop the caller's tools, not ride on until the
            // stream closes.
            await using var h = new ChatEndpointHost();
            await h.InitializeAsync();
            await h.ExecAsync(
                "CREATE TABLE app_users (id TEXT PRIMARY KEY, email TEXT, password_hash TEXT, " +
                "display_name TEXT, tenant_id TEXT, roles TEXT)");
            await h.ExecAsync("INSERT INTO app_users VALUES ('user-1', @email, @hash, 'Alice', 'tenant-a', 'editor')",
                ("@email", Login), ("@hash", CheapHasher.HashPassword(Login, Password)));
            var connector = new RecordingConnector();
            var client = await h.StartAsync(
                connectors: new IChatConnector[] { connector }, messagesAsExploreConnector: true,
                configureServices: services =>
                {
                    services.AddBifrostLocalAuth(h.ConnectionString, o => o.RequireMfa = false);
                    services.AddSingleton(sp => new LocalUserStore(
                        sp.GetRequiredService<IDbConnFactory>(), sp.GetRequiredService<LocalAuthOptions>(), CheapHasher));
                },
                defaultAuthScheme: CookieAuthenticationDefaults.AuthenticationScheme,
                configureApp: app => app.UseBifrostLocalAuth());

            using var login = await client.PostAsJsonAsync("/auth/login", new { login = Login, password = Password });
            login.StatusCode.Should().Be(HttpStatusCode.NoContent);
            var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

            h.Fake.OptionsScript = (_, options, ct) => Script(options!, ct);
            async IAsyncEnumerable<ChatCompletionEvent> Script(
                ChatCompletionRequestOptions options, [EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return new ChatCompletionDelta("Checking. ");
                if (revokeMidStream)
                    await h.Services.GetRequiredService<LocalAuthAdministration>()
                        .RevokeAllSessionsAsync("user-1", "admin-1", CancellationToken.None);
                await options.Tools!.Executor.ExecuteAsync("fake_messages", "{}", ct);
                yield return new ChatCompletionResult("Checking. Done.", ChatCompletionStopReason.Complete, null, 1, 1);
            }

            using var create = new HttpRequestMessage(HttpMethod.Post, "/_chat/conversations")
            {
                Content = JsonContent.Create(new { }),
            };
            create.Headers.Add("Cookie", cookie);
            using var created = await client.SendAsync(create);
            created.StatusCode.Should().Be(HttpStatusCode.OK);
            var conversationId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
                .RootElement.GetProperty("id").GetInt64();

            using var post = new HttpRequestMessage(HttpMethod.Post, $"/_chat/conversations/{conversationId}/messages")
            {
                Content = JsonContent.Create(new { content = "What is there?" }),
            };
            post.Headers.Add("Cookie", cookie);
            using var response = await client.SendAsync(post);
            var events = SseReader.Parse(await response.Content.ReadAsStringAsync());

            if (!revokeMidStream)
            {
                events.Select(e => e.Name).Should().Equal(new[] { "message-accepted", "delta", "done" },
                    "control: a live session runs its tool and completes");
                connector.Executions.Should().Be(1);
                return;
            }

            connector.Executions.Should().Be(0, "a tool call after the session ended must not run");
            events.Select(e => e.Name).Should().Equal("message-accepted", "delta", "error");
            events[2].Data.GetProperty("code").GetString().Should().Be("session-ended");
            (await h.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 'assistant'")).Should().Be(0L);
        }

        [Theory]
        [InlineData("alice@club.test", true)]
        [InlineData("bob@club.test", false)]
        public async Task ChatStream_RunsNoToolForAnEnrollmentOnlySession(string login, bool served)
        {
            // Bob is a viewer who owes a second factor he has not enrolled: his sign-in gives an
            // enrollment-only session, which may reach enrollment and logout and nothing else. The
            // cookie hook would run his chat request anonymous; the stream must not lean on that, so a
            // test hook adopts his cookie's principal unchecked and the per-tool-call check alone must
            // refuse it. Alice (editor, no requirement) is the control over the same hook.
            const string uncheckedCookie = "X-Test-Unchecked-Cookie";
            await using var h = new ChatEndpointHost();
            await h.InitializeAsync();
            await h.ExecAsync(
                "CREATE TABLE app_users (id TEXT PRIMARY KEY, email TEXT, password_hash TEXT, " +
                "display_name TEXT, tenant_id TEXT, roles TEXT)");
            await h.ExecAsync("INSERT INTO app_users VALUES ('user-1', @email, @hash, 'Alice', 'tenant-a', 'editor')",
                ("@email", "alice@club.test"), ("@hash", CheapHasher.HashPassword("alice@club.test", Password)));
            await h.ExecAsync("INSERT INTO app_users VALUES ('user-2', @email, @hash, 'Bob', 'tenant-a', 'viewer')",
                ("@email", "bob@club.test"), ("@hash", CheapHasher.HashPassword("bob@club.test", Password)));
            var connector = new RecordingConnector();
            var client = await h.StartAsync(
                connectors: new IChatConnector[] { connector }, messagesAsExploreConnector: true,
                configureServices: services =>
                {
                    services.AddSingleton<IRootKeyProvider>(new ConfigRootKeyProvider(RandomNumberGenerator.GetBytes(FieldCipher.KeySize)));
                    services.AddSingleton<IDataEncryptionKeyStore>(new InMemoryDataEncryptionKeyStore());
                    services.AddBifrostLocalAuth(h.ConnectionString, o =>
                    {
                        o.RequireMfa = false;
                        o.MfaKeyRef = "config:mfa";
                        o.RequireMfaForRoles = new List<string> { "viewer" };
                    });
                    services.AddSingleton(sp => new LocalUserStore(
                        sp.GetRequiredService<IDbConnFactory>(), sp.GetRequiredService<LocalAuthOptions>(), CheapHasher));
                },
                defaultAuthScheme: CookieAuthenticationDefaults.AuthenticationScheme,
                configureApp: app =>
                {
                    app.Use((context, next) =>
                    {
                        if (context.Request.Headers.TryGetValue(uncheckedCookie, out var raw))
                        {
                            var format = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                                .Get(CookieAuthenticationDefaults.AuthenticationScheme).TicketDataFormat;
                            context.User = format.Unprotect(raw.ToString().Split('=', 2)[1])!.Principal;
                        }
                        return next();
                    });
                    app.UseBifrostLocalAuth();
                });

            using var signIn = await client.PostAsJsonAsync("/auth/login", new { login, password = Password });
            signIn.IsSuccessStatusCode.Should().BeTrue();
            if (!served)
                (await signIn.Content.ReadAsStringAsync()).Should().Contain("mfa-enrollment-required");
            var cookie = signIn.Headers.GetValues("Set-Cookie")
                .Single(c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal)).Split(';')[0];

            h.Fake.OptionsScript = (_, options, ct) => Script(options!, ct);
            async IAsyncEnumerable<ChatCompletionEvent> Script(
                ChatCompletionRequestOptions options, [EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return new ChatCompletionDelta("Checking. ");
                await options.Tools!.Executor.ExecuteAsync("fake_messages", "{}", ct);
                yield return new ChatCompletionResult("Checking. Done.", ChatCompletionStopReason.Complete, null, 1, 1);
            }

            using var create = new HttpRequestMessage(HttpMethod.Post, "/_chat/conversations") { Content = JsonContent.Create(new { }) };
            create.Headers.Add(uncheckedCookie, cookie);
            using var created = await client.SendAsync(create);
            created.StatusCode.Should().Be(HttpStatusCode.OK);
            var conversationId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
                .RootElement.GetProperty("id").GetInt64();

            using var post = new HttpRequestMessage(HttpMethod.Post, $"/_chat/conversations/{conversationId}/messages")
            {
                Content = JsonContent.Create(new { content = "What is there?" }),
            };
            post.Headers.Add(uncheckedCookie, cookie);
            using var response = await client.SendAsync(post);
            var events = SseReader.Parse(await response.Content.ReadAsStringAsync());

            if (served)
            {
                events.Select(e => e.Name).Should().Equal(new[] { "message-accepted", "delta", "done" },
                    "control: a full session over the same hook runs its tool and completes");
                connector.Executions.Should().Be(1);
                return;
            }
            connector.Executions.Should().Be(0, "an enrollment-only session must not run a tool");
            events.Select(e => e.Name).Should().Equal("message-accepted", "delta", "error");
            events[2].Data.GetProperty("code").GetString().Should().Be("session-ended");
        }

        private sealed class RecordingConnector : IChatConnector
        {
            private int _executions;
            public int Executions => _executions;
            public int Priority => 200;

            public IReadOnlyList<ChatToolDefinition> GetToolDefinitions(IDbModel model, ChatConnectorBinding binding)
                => new[] { new ChatToolDefinition($"fake_{binding.Table.DbName}", "Rows.", """{"type":"object","properties":{}}""") };

            public Task<ChatToolResult> ExecuteAsync(
                string toolName, string inputJson, IDictionary<string, object?> authContext, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _executions);
                return Task.FromResult(new ChatToolResult { TextPayload = """{"rows":[]}""" });
            }
        }
    }
}
