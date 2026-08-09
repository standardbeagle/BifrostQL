using System.IO.Pipelines;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// Invariant 3 (.claude/rules/protocol-adapter-security.md) on the MCP wire: no
    /// Bifrost-internal exception message may be forwarded verbatim to a caller.
    ///
    /// <para>The funnel's condition set mixes two families. <c>ToolPromptException</c> and
    /// <c>McpIdentityException</c> are types this adapter OWNS and authors — their text is
    /// the answer the calling agent needs, and invariant 3 explicitly permits forwarding
    /// them. <c>BifrostExecutionError</c> is Bifrost-internal and carries no type-level
    /// signal separating a curated instance from one naming a schema-qualified table, a
    /// context key, or raw driver text: <c>TenantFilterTransformer</c> is the proof, tagging
    /// its fail-closed denial <c>AccessDeniedCode</c> while embedding both the qualified
    /// table name and the tenant context-key name.</para>
    ///
    /// <para>The caller here is an LLM agent, so an unusable error is a real cost: the
    /// sanitized answer keeps a STABLE MACHINE-READABLE CODE and a category specific enough
    /// to recover from (retry a different table, ask the user for the missing context) —
    /// what it must never keep is a schema identifier, a context-key name, or DB text. An
    /// agent does not need <c>main.orders</c> to know it was denied.</para>
    /// </summary>
    public sealed class McpErrorSanitizationTests
    {
        /// <summary>The wire contract, pinned as literals: an agent parses these, so they are
        /// part of the adapter's public surface and must not drift silently.</summary>
        private const string AccessDeniedCode = "access_denied";
        private const string ExecutionErrorCode = "execution_error";

        /// <summary>The real <c>TenantFilterTransformer</c> denial, verbatim.</summary>
        private const string TenantDenialMessage =
            "Tenant context required but not found. Expected 'tenant_id' in user context for table 'main.orders'.";

        /// <summary>A <c>BifrostExecutionError</c> in its DB-wrapping shape.</summary>
        private const string DatabaseFaultMessage =
            "Invalid column name 'patient_ssn' on 'clinical.patients' (Npgsql 42703).";

        /// <summary>An executor whose model resolution fails with a caller-supplied condition —
        /// the one condition provably shared by every op class, since all of them resolve the
        /// endpoint's cached model.</summary>
        private sealed class ThrowingExecutor(Func<Exception> fault) : IQueryIntentExecutor
        {
            public Task<IDbModel> GetModelAsync(string? endpoint = null) => throw fault();

            public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
                => throw fault();
        }

        private static Exception TenantDenial() =>
            new BifrostExecutionError(TenantDenialMessage) { ErrorCode = BifrostExecutionError.AccessDeniedCode };

        private static Exception DatabaseFault() => new BifrostExecutionError(DatabaseFaultMessage);

        [Theory]
        [InlineData("bifrost_query")]
        [InlineData("bifrost_row_context")]
        [InlineData("bifrost_aggregate")]
        [InlineData("bifrost_search")]
        [InlineData("bifrost_schema_overview")]
        [InlineData("bifrost_describe_table")]
        public async Task ATenantDenial_LeaksNeitherTheTableNorTheContextKey_OnAnyToolOpClass(string toolName)
        {
            await WithClientAsync(TenantDenial, async client =>
            {
                var text = await CallTextAsync(client, toolName);

                text.Should().NotContain("tenant_id", "the wire must not name a context key or column");
                text.Should().NotContain("main.orders", "the wire must not name a schema-qualified table");
                text.Should().NotContain(TenantDenialMessage);

                // Parity (invariant 10): the SAME condition must carry the SAME stable code on
                // every op class, and it must stay actionable enough for an agent to recover.
                text.Should().Contain(AccessDeniedCode,
                    "a denial keeps a stable machine-readable code — the actionable signal");
            });
        }

        [Theory]
        [InlineData("bifrost_query")]
        [InlineData("bifrost_schema_overview")]
        public async Task ADatabaseFault_LeaksNeitherTheColumnNorTheDriverText(string toolName)
        {
            await WithClientAsync(DatabaseFault, async client =>
            {
                var text = await CallTextAsync(client, toolName);

                text.Should().NotContain("patient_ssn");
                text.Should().NotContain("clinical.patients");
                text.Should().NotContain("Npgsql");
                text.Should().Contain(ExecutionErrorCode);
            });
        }

        [Fact]
        public async Task ADenialAndAFault_CarryDistinctCodes_SoAnAgentCanTellRetryableFromNot()
        {
            // Both halves are asserted on each condition: without the Contain, this fact would
            // hold vacuously against an unsanitized funnel (neither code appears at all).
            await WithClientAsync(TenantDenial, async client =>
                (await CallTextAsync(client, "bifrost_query"))
                    .Should().Contain(AccessDeniedCode).And.NotContain(ExecutionErrorCode));

            await WithClientAsync(DatabaseFault, async client =>
                (await CallTextAsync(client, "bifrost_query"))
                    .Should().Contain(ExecutionErrorCode).And.NotContain(AccessDeniedCode));
        }

        [Fact]
        public async Task TheResourceOpClasses_CarryTheSameSanitizedTextAsTheToolOpClasses()
        {
            await WithClientAsync(TenantDenial, async client =>
            {
                var read = (await client.Invoking(c => c.ReadResourceAsync("bifrost://schema/orders").AsTask())
                    .Should().ThrowAsync<McpException>()).Which.Message;
                var list = (await client.Invoking(c => c.ListResourcesAsync().AsTask())
                    .Should().ThrowAsync<McpException>()).Which.Message;

                foreach (var message in new[] { read, list })
                {
                    message.Should().NotContain("tenant_id");
                    message.Should().NotContain("main.orders");
                    message.Should().Contain(AccessDeniedCode,
                        "resources have no isError shape, but the MESSAGE must be the identical mapping");
                }
            });
        }

        [Fact]
        public async Task AnAdapterOwnedPromptError_IsStillForwardedVerbatim()
        {
            // ToolPromptException is a type this adapter OWNS: its message is authored as a
            // prompt the agent acts on (did-you-mean, allowed values). Sanitizing it would
            // destroy the one distinction worth keeping — invariant 3 permits it explicitly.
            const string prompt = "Invalid detail 'summry'. Allowed values: summary, full.";
            await WithClientAsync(() => new ToolPromptException(prompt), async client =>
                (await CallTextAsync(client, "bifrost_query")).Should().Contain(prompt));
        }

        [Fact]
        public async Task AnIdentityFailure_IsStillForwardedVerbatim()
        {
            // McpIdentityException's message is an adapter-owned CONSTANT: it names no issuer,
            // token, session or user, so there is nothing to sanitize.
            await WithClientAsync(() => new McpIdentityException(), async client =>
                (await CallTextAsync(client, "bifrost_query"))
                    .Should().Contain("did not present a valid identity"));
        }

        private static async Task<string> CallTextAsync(McpClient client, string toolName)
        {
            var result = await client.CallToolAsync(toolName, new Dictionary<string, object?>
            {
                ["table"] = "orders",
                ["id"] = 1,
                ["term"] = "probe",
                ["group_by"] = "name",
            });

            // Asserting only IsError would be VACUOUS: the MCP SDK wraps an escaped handler
            // exception into an isError result all by itself. The MESSAGE is what differs.
            result.IsError.Should().BeTrue();
            return result.Content.OfType<TextContentBlock>().Single().Text;
        }

        private static async Task WithClientAsync(Func<Exception> fault, Func<McpClient, Task> body)
        {
            var options = BifrostMcpServerFactory.CreateServerOptions(new ThrowingExecutor(fault));
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), serverName: "BifrostQL-sanitize-test");
            await using var server = McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
            using var stop = new CancellationTokenSource();
            var run = server.RunAsync(stop.Token);
            var client = await McpClient.CreateAsync(new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream()));
            try { await body(client); }
            finally
            {
                await client.DisposeAsync();
                await stop.CancelAsync();
                try { await run; } catch (OperationCanceledException) { }
            }
        }
    }
}
