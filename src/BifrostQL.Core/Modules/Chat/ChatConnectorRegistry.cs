using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// Collects the DI-registered <see cref="IChatConnector"/>s (priority order,
    /// registration order on ties) and builds the per-model <see cref="ChatToolSet"/>
    /// the tool loop runs against. Building is fail-fast: a tool name defined twice —
    /// by two connectors or twice by one — is rejected with an error naming both
    /// connectors and the tool, and a name Claude would reject (outside
    /// <c>[a-zA-Z0-9_-]{1,64}</c>) never reaches the wire. These faults surface on the
    /// first tool-set build for a model (the earliest point the model exists), not on
    /// a later tool call.
    /// </summary>
    public sealed class ChatConnectorRegistry
    {
        private static readonly Regex ValidToolName = new("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled);

        private readonly IReadOnlyList<IChatConnector> _connectors;

        public ChatConnectorRegistry(IEnumerable<IChatConnector> connectors)
        {
            if (connectors is null)
                throw new ArgumentNullException(nameof(connectors));
            _connectors = connectors.OrderBy(c => c.Priority).ToList();
        }

        /// <summary>The registered connectors in priority order (ascending, stable).</summary>
        public IReadOnlyList<IChatConnector> Connectors => _connectors;

        /// <summary>
        /// Builds the tool set for <paramref name="model"/>: every connector table
        /// (<see cref="ChatConnectorConfig.FromModel"/>) offered to every connector in
        /// priority order. A model with no connector tables — or no registered
        /// connectors — yields an empty set, and the completion request carries no
        /// tools at all.
        /// </summary>
        public ChatToolSet BuildToolSet(IDbModel model)
        {
            if (model is null)
                throw new ArgumentNullException(nameof(model));

            var bindings = ChatConnectorConfig.FromModel(model);
            var tools = new Dictionary<string, ChatToolSet.RegisteredTool>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (var connector in _connectors)
            {
                foreach (var binding in bindings)
                {
                    foreach (var definition in connector.GetToolDefinitions(model, binding))
                    {
                        if (!ValidToolName.IsMatch(definition.Name))
                            throw new InvalidOperationException(
                                $"Chat tool '{definition.Name}' from connector '{connector.GetType().Name}' has an " +
                                "invalid name; tool names must match [a-zA-Z0-9_-] and be at most 64 characters.");

                        if (tools.TryGetValue(definition.Name, out var existing))
                            throw new InvalidOperationException(
                                $"Chat tool name '{definition.Name}' is defined by both " +
                                $"'{existing.Connector.GetType().Name}' and '{connector.GetType().Name}'; " +
                                "tool names must be unique across all chat connectors. Rename one of the tools.");

                        tools.Add(definition.Name, new ChatToolSet.RegisteredTool(connector, definition));
                        order.Add(definition.Name);
                    }
                }
            }

            return new ChatToolSet(order.Select(name => tools[name]).ToList());
        }
    }

    /// <summary>
    /// The resolved tools for one model: the definitions sent to the model plus the
    /// name→connector dispatch behind them. Auth is bound per request through
    /// <see cref="CreateExecutor"/> so every execution carries the caller's identity
    /// explicitly.
    /// </summary>
    public sealed class ChatToolSet
    {
        internal sealed record RegisteredTool(IChatConnector Connector, ChatToolDefinition Definition);

        private readonly IReadOnlyList<RegisteredTool> _tools;
        private readonly IReadOnlyDictionary<string, RegisteredTool> _byName;

        internal ChatToolSet(IReadOnlyList<RegisteredTool> tools)
        {
            _tools = tools;
            _byName = tools.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);
            Definitions = tools.Select(t => t.Definition).ToList();
        }

        /// <summary>The tool definitions in connector-priority order.</summary>
        public IReadOnlyList<ChatToolDefinition> Definitions { get; }

        /// <summary>True when no connector exposed any tool for the model.</summary>
        public bool IsEmpty => _tools.Count == 0;

        /// <summary>
        /// Dispatches one tool call to the connector that defined the tool. An unknown
        /// tool name (a model hallucination — the API constrains calls to the supplied
        /// tools, so this should not occur) throws; the tool loop converts the throw
        /// into an <c>is_error</c> result the model can recover from.
        /// </summary>
        public Task<ChatToolResult> ExecuteAsync(
            string toolName,
            string inputJson,
            IDictionary<string, object?> authContext,
            CancellationToken cancellationToken)
        {
            if (authContext is null)
                throw new ArgumentNullException(nameof(authContext));
            if (!_byName.TryGetValue(toolName, out var tool))
                throw new InvalidOperationException(
                    $"Unknown chat tool '{toolName}'; no registered connector defines it.");
            return tool.Connector.ExecuteAsync(toolName, inputJson ?? "{}", authContext, cancellationToken);
        }

        /// <summary>
        /// Binds the caller's auth context into an executor the completion layer can
        /// invoke without holding an identity of its own.
        /// </summary>
        public IChatToolExecutor CreateExecutor(IDictionary<string, object?> authContext)
        {
            if (authContext is null)
                throw new ArgumentNullException(nameof(authContext));
            return new BoundExecutor(this, authContext);
        }

        private sealed class BoundExecutor : IChatToolExecutor
        {
            private readonly ChatToolSet _tools;
            private readonly IDictionary<string, object?> _authContext;

            public BoundExecutor(ChatToolSet tools, IDictionary<string, object?> authContext)
            {
                _tools = tools;
                _authContext = authContext;
            }

            public Task<ChatToolResult> ExecuteAsync(string toolName, string inputJson, CancellationToken cancellationToken)
                => _tools.ExecuteAsync(toolName, inputJson, _authContext, cancellationToken);
        }
    }
}
