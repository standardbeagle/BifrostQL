using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Invariant 3 (.claude/rules/protocol-adapter-security.md) on the chat wire: no
    /// Bifrost-internal exception message may be forwarded verbatim to a client.
    ///
    /// <para>There is no type-level signal separating a curated user-facing
    /// <c>BifrostExecutionError</c> from one carrying schema or infrastructure detail —
    /// <c>TenantFilterTransformer</c> proves it, tagging a denial with the same
    /// <c>AccessDeniedCode</c> the policy transformer uses while embedding the fully
    /// qualified table name and the tenant context-key name in the message. Nor is
    /// <c>InvalidOperationException.Message</c> curated for a wire. Both must be logged
    /// server-side and answered with an adapter-owned, generic message.</para>
    ///
    /// <para>Status codes are deliberately unchanged: 403 for a denial and 404 for a
    /// missing conversation are the actionable, non-enumerating signals the caller needs.
    /// Only the MESSAGE is sanitized.</para>
    /// </summary>
    public sealed class ChatErrorSanitizationTests : IAsyncLifetime
    {
        private readonly ChatEndpointHost _h = new();

        public Task InitializeAsync() => _h.InitializeAsync();
        public async Task DisposeAsync() => await _h.DisposeAsync();

        [Fact]
        public async Task A_tenant_denial_does_not_leak_the_table_or_context_key_name()
        {
            var client = await _h.StartAsync();
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");

            // Authenticated but tenant-less: TenantFilterTransformer throws
            // "Tenant context required but not found. Expected 'tenant_id' in user context
            //  for table 'main.messages'." tagged AccessDeniedCode.
            using var response = await client.SendAsync(ChatEndpointHost.Post(
                $"/_chat/conversations/{conversationId}/messages", new { content = "no tenant" },
                "user-x", tenant: null));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var message = await ErrorMessage(response);
            message.Should().NotBeNullOrWhiteSpace("a denial still needs an actionable answer");
            message.Should().NotContain("messages", "the wire must not name a table");
            message.Should().NotContain("conversations", "the wire must not name a table");
            message.Should().NotContain("main.", "the wire must not name a schema");
            message.Should().NotContain("tenant_id", "the wire must not name a context key or column");
        }

        [Fact]
        public async Task A_conversation_create_denial_does_not_leak_internal_detail()
        {
            var client = await _h.StartAsync();

            using var response = await client.SendAsync(ChatEndpointHost.Post(
                "/_chat/conversations", new { title = "t" }, "user-x", tenant: null));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var message = await ErrorMessage(response);
            message.Should().NotContain("conversations");
            message.Should().NotContain("main.");
            message.Should().NotContain("tenant_id");
        }

        [Fact]
        public async Task A_not_found_conversation_keeps_its_adapter_owned_message()
        {
            // The 404 path already answers with a message this adapter owns; sanitizing the
            // others must not collapse it into the same opaque string, or the endpoint loses
            // the one distinction that is genuinely useful to a caller.
            var client = await _h.StartAsync();

            using var response = await client.SendAsync(ChatEndpointHost.Post(
                "/_chat/conversations/999999/messages", new { content = "hi" }, "user-a", "tenant-a"));

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ErrorMessage(response)).Should().Be("Conversation not found.");
        }

        private static async Task<string> ErrorMessage(HttpResponseMessage response)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("message").GetString() ?? string.Empty;
        }
    }
}
