using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace BifrostQL.Mcp
{
    /// <summary>
    /// Hosts a BifrostQL MCP server over stdio as an <see cref="IProtocolAdapter"/>.
    /// Register with <c>AddBifrostQL(o =&gt; o.AddProtocolAdapter&lt;BifrostMcpAdapter&gt;())</c>;
    /// the host process's stdin/stdout then speak MCP (JSON-RPC), so nothing else
    /// in the process may write to stdout.
    ///
    /// <para><b>Identity (stdio dev mode).</b> A stdio MCP session carries no
    /// per-request principal: the caller is whoever launched the process, so the
    /// session's effective identity is scoped to the endpoint's connection string
    /// and every request executes with an <b>empty user context</b> — there is no
    /// tenant id, user id, or role projection. The schema tools this adapter
    /// exposes only read the cached <see cref="Core.Model.IDbModel"/> (metadata,
    /// never rows), which requires no tenant. Any future row-reading tool executes
    /// through <see cref="IQueryIntentExecutor"/>, where a tenant-filtered table
    /// combined with this empty context fails closed ("Tenant context required")
    /// exactly like an unauthenticated GraphQL request — never an anonymous
    /// pass-through.</para>
    ///
    /// <para>The MCP surface itself is built by
    /// <see cref="BifrostMcpServerFactory.CreateServerOptions"/>; this class owns
    /// only the stdio transport lifecycle.</para>
    /// </summary>
    public sealed class BifrostMcpAdapter : IProtocolAdapter
    {
        private readonly IQueryIntentExecutor _executor;
        private readonly ILoggerFactory _loggerFactory;
        private readonly CancellationTokenSource _stopping = new();
        private McpServer? _server;
        private Task? _runTask;

        public BifrostMcpAdapter(IQueryIntentExecutor executor, ILoggerFactory? loggerFactory = null)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_server is not null)
                throw new InvalidOperationException("BifrostMcpAdapter is already started.");

            var options = BifrostMcpServerFactory.CreateServerOptions(_executor);
            var transport = new StdioServerTransport(BifrostMcpServerFactory.ServerName, _loggerFactory);
            _server = McpServer.Create(transport, options, _loggerFactory, serviceProvider: null);

            // The session outlives StartAsync: it runs until the client closes
            // stdin or the host stops. Faults surface on StopAsync (graceful
            // shutdown always awaits the session) rather than being swallowed.
            _runTask = _server.RunAsync(_stopping.Token);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_server is null || _runTask is null)
                return;

            await _stopping.CancelAsync();
            try
            {
                await _runTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the normal stdio shutdown path.
            }
            finally
            {
                await _server.DisposeAsync();
                _server = null;
                _runTask = null;
                _stopping.Dispose();
            }
        }
    }
}
