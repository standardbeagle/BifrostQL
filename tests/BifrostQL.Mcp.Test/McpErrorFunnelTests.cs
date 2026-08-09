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
    /// Cross-op-class error-mapping parity for the MCP front door
    /// (protocol-adapter-security invariants 9 and 10): every tools/call op class must
    /// route through ONE funnel, so the SAME underlying condition yields the SAME wire
    /// signal regardless of which tool was invoked.
    ///
    /// <para>Before the funnel, only the data tools (<c>bifrost_query</c>,
    /// <c>bifrost_row_context</c>, <c>bifrost_aggregate</c>, <c>bifrost_search</c>) and the
    /// declarative tools carried a catch. The schema tools
    /// (<c>bifrost_schema_overview</c>, <c>bifrost_describe_table</c>) and the resource
    /// handlers sat OUTSIDE it, so an identical condition surfaced as a sanitized
    /// <c>isError</c> result on one tool and an unhandled JSON-RPC fault on another — a
    /// differential wire signal for one condition, exactly what invariant 9 forbids.</para>
    ///
    /// <para>The condition used here is an endpoint/model resolution failure — the one
    /// condition every op class provably shares, since every op class resolves the
    /// endpoint's cached model.</para>
    /// </summary>
    public sealed class McpErrorFunnelTests
    {
        private const string FaultMessage = "endpoint model unavailable";

        /// <summary>The stable wire code the funnel maps an untagged
        /// <see cref="BifrostExecutionError"/> onto — identical on every op class.</summary>
        private const string SanitizedCode = "execution_error";

        /// <summary>An executor whose model resolution always fails, exactly as a broken
        /// endpoint/connection does on the real path.</summary>
        private sealed class FailingExecutor : IQueryIntentExecutor
        {
            public Task<IDbModel> GetModelAsync(string? endpoint = null)
                => throw new BifrostExecutionError(FaultMessage);

            public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
                => throw new BifrostExecutionError(FaultMessage);
        }

        [Theory]
        [InlineData("bifrost_query")]
        [InlineData("bifrost_row_context")]
        [InlineData("bifrost_aggregate")]
        [InlineData("bifrost_search")]
        [InlineData("bifrost_schema_overview")]
        [InlineData("bifrost_describe_table")]
        public async Task EveryToolOpClass_MapsTheSameConditionToTheSameWireSignal(string toolName)
        {
            await WithClientAsync(async client =>
            {
                var result = await client.CallToolAsync(toolName, new Dictionary<string, object?>
                {
                    ["table"] = "orders",
                    ["id"] = 1,
                    ["term"] = "probe",
                    ["group_by"] = "name",
                });

                result.IsError.Should().BeTrue(
                    $"{toolName} must route through the same funnel as every other tools/call op class, " +
                    "never escape as an unhandled JSON-RPC fault");

                // Asserting only IsError would be VACUOUS: the MCP SDK already wraps an
                // escaped handler exception into an isError result, so that assertion holds
                // with or without the funnel. What actually differs is the MESSAGE — the
                // funnel's own mapped text versus the SDK's generic wrapper — and a
                // differential message for one condition across sibling op classes is
                // exactly the wire signal invariant 9 forbids.
                //
                // The mapped text is the funnel's SANITIZED code, not the exception's own
                // message: a BifrostExecutionError is Bifrost-internal and never reaches the
                // wire verbatim (invariant 3, McpErrorSanitizationTests). Both halves matter
                // here — the raw text must be absent (no leak) AND the stable code present on
                // every op class (parity).
                var text = result.Content.OfType<TextContentBlock>().Single().Text;
                text.Should().NotContain(FaultMessage,
                    $"{toolName} must not forward Bifrost-internal exception text");
                text.Should().Contain(SanitizedCode,
                    $"{toolName} must map the condition through the seam's own funnel, " +
                    "producing the identical text every other op class produces");
            });
        }

        [Fact]
        public async Task ResourceHandlers_MapTheSameConditionToAMappedProtocolError()
        {
            await WithClientAsync(async client =>
            {
                // Resources have no isError shape, so the funnel maps the condition onto a
                // protocol error — but through the SAME mapping, never as an unhandled throw.
                var read = () => client.ReadResourceAsync("bifrost://schema/orders").AsTask();
                (await read.Should().ThrowAsync<McpException>()).Which.Message
                    .Should().Contain(SanitizedCode).And.NotContain(FaultMessage);

                var list = () => client.ListResourcesAsync().AsTask();
                (await list.Should().ThrowAsync<McpException>()).Which.Message
                    .Should().Contain(SanitizedCode).And.NotContain(FaultMessage);
            });
        }

        private static async Task WithClientAsync(Func<McpClient, Task> body)
        {
            var options = BifrostMcpServerFactory.CreateServerOptions(new FailingExecutor());
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), serverName: "BifrostQL-funnel-test");
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
