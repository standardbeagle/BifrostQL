using System.IO.Pipelines;
using System.Text.Json;
using BifrostQL.AdapterConformance;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// Runs the shared protocol-adapter security-conformance suite against the MCP front door.
    /// Every read and write is issued as REAL MCP tool traffic — a JSON-RPC tools/call over an
    /// in-memory stream transport, through the official SDK client/server pair — so the facts prove
    /// the TOOL LAYER (bifrost_query / bifrost_insert / bifrost_update / bifrost_delete), not a
    /// shortcut into <see cref="IQueryIntentExecutor"/> / <see cref="IMutationIntentExecutor"/>. The
    /// MCP server is bound to the base fixture's real transformer-pipeline executors and SQL-capture
    /// observer (nothing is registered on the HTTP endpoint options, like the RESP derivation), so
    /// tenant isolation, soft-delete, policy read guards, parameterization and the mutation
    /// transformer chain are all proven on the wire.
    ///
    /// <para><b>Writes.</b> <see cref="AdapterSupportsMutations"/> is true — the write tools MUST
    /// prove tenant pinning, cross-tenant no-op, soft-delete and fail-closed identity — and the MCP
    /// wire has a genuine row-creating verb (bifrost_insert), so <see cref="AdapterSupportsInserts"/>
    /// stays true. Rejections surface as the tool's own error text, which forwards the server's
    /// <see cref="BifrostExecutionError"/> message (the actionable agent-facing contract MCP already
    /// ships), so the canonical rejection fragments match with no override.</para>
    /// </summary>
    public sealed class McpProtocolAdapterConformanceTests : ProtocolAdapterConformanceTests
    {
        // MCP is driven on its own in-memory front door bound to the base fixture's real executors,
        // so nothing is registered on the HTTP endpoint options.
        protected override void RegisterAdapter(BifrostMultiDbOptions options) { }

        protected override bool AdapterSupportsMutations => true;

        protected override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReadAsync(
            ConformanceReadRequest request)
        {
            var args = new Dictionary<string, object?>
            {
                ["table"] = request.Table,
                ["fields"] = request.Columns,
            };
            if (request.Filter is not null)
                args["filter"] = request.Filter;

            var payload = await CallToolAsync(request.Principal, enableWrites: false, "bifrost_query", args);
            return payload.GetProperty("rows").EnumerateArray()
                .Select(row => (IReadOnlyDictionary<string, object?>)row.EnumerateObject()
                    .ToDictionary(p => p.Name, p => DecodeJson(p.Value), StringComparer.Ordinal))
                .ToList();
        }

        protected override async Task<object?> ExecuteMutationAsync(ConformanceMutationRequest request)
        {
            var (tool, args) = request.Action switch
            {
                ConformanceMutationAction.Insert => ("bifrost_insert", new Dictionary<string, object?>
                {
                    ["table"] = request.Table,
                    ["values"] = request.Data,
                }),
                ConformanceMutationAction.Update => ("bifrost_update", new Dictionary<string, object?>
                {
                    ["table"] = request.Table,
                    ["id"] = request.PrimaryKey,
                    ["set"] = request.Data,
                }),
                ConformanceMutationAction.Delete => ("bifrost_delete", new Dictionary<string, object?>
                {
                    ["table"] = request.Table,
                    ["id"] = request.PrimaryKey,
                }),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.Action, "unknown mutation action"),
            };

            var payload = await CallToolAsync(request.Principal, enableWrites: true, tool, args);
            return DecodeJson(payload.GetProperty("result"));
        }

        /// <summary>
        /// Spins up a fresh in-memory MCP server bound to the base fixture's real executors with the
        /// caller's identity, issues one tools/call over the wire, and returns the structured payload.
        /// A tool error is re-thrown carrying the server's message so the kit's fail-closed facts see
        /// a real rejection (never swallowed) — exactly the contract the base class requires.
        /// </summary>
        private async Task<JsonElement> CallToolAsync(
            System.Security.Claims.ClaimsPrincipal? principal, bool enableWrites, string tool, Dictionary<string, object?> args)
        {
            var executor = Host.Services.GetRequiredService<IQueryIntentExecutor>();
            var mutation = Host.Services.GetRequiredService<IMutationIntentExecutor>();
            var factory = Host.Services.GetRequiredService<IBifrostAuthContextFactory>();

            // Host.Services is the root provider (never disposed), so a null principal projects an
            // empty (fail-closed) context and a tenant principal projects its scope — the same shared
            // factory seam every transport uses.
            var provider = BifrostMcpAdapter.CreateProjectionProvider(factory, Host.Services, principal);
            var options = BifrostMcpServerFactory.CreateServerOptions(
                executor, EndpointPath, userContextProvider: provider,
                mutationExecutor: mutation, enableWrites: enableWrites);

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), serverName: "BifrostQL-conformance");
            await using var server = McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
            using var stop = new CancellationTokenSource();
            var run = server.RunAsync(stop.Token);
            var client = await McpClient.CreateAsync(new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream()));
            try
            {
                var result = await client.CallToolAsync(tool, args);
                if (result.IsError == true)
                    throw new InvalidOperationException(result.Content.OfType<TextContentBlock>().Single().Text);
                return result.StructuredContent!.Value;
            }
            finally
            {
                await client.DisposeAsync();
                await stop.CancelAsync();
                try { await run; } catch (OperationCanceledException) { }
            }
        }

        private static object? DecodeJson(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };
    }
}
