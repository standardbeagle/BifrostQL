using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// A chat connector turns connector-marked tables (<see cref="ChatConnectorConfig"/>)
    /// into Claude tools the chat LLM can call mid-conversation. Implementations are
    /// registered in DI (<c>AddChatConnector&lt;T&gt;</c> on the host options, mirroring
    /// <c>AddFilterTransformer</c>) and collected by <see cref="ChatConnectorRegistry"/>.
    /// A connector owns both sides of its tools: the definitions the model sees and the
    /// execution behind them. Execution receives the caller's auth context explicitly on
    /// every call — there is no ambient identity — so reads/writes ride the intent
    /// executors under the caller's row scope exactly like any other transport.
    /// </summary>
    public interface IChatConnector
    {
        /// <summary>
        /// Orders connectors when building the tool set (ascending; ties keep
        /// registration order). Same bands as the module system: 0-99 built-in,
        /// 100+ application connectors.
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// The tools this connector exposes for one connector table. Descriptions must
        /// be prescriptive about WHEN the model should call the tool (not just what it
        /// does), and must incorporate the table's <c>chat-tool-description</c> metadata
        /// (<see cref="ChatConnectorConfig.ToolDescription"/>) when present — that key
        /// exists so the schema author can steer the model. Return an empty list for a
        /// binding whose connector types this connector does not serve.
        /// </summary>
        IReadOnlyList<ChatToolDefinition> GetToolDefinitions(IDbModel model, ChatConnectorBinding binding);

        /// <summary>
        /// Executes one tool call. <paramref name="toolName"/> is always a name this
        /// connector returned from <see cref="GetToolDefinitions"/>;
        /// <paramref name="inputJson"/> is the model-supplied argument object as JSON
        /// (never null, <c>{}</c> when the model sent no arguments) and must be
        /// re-validated — the model is an untrusted caller. Failures may throw: the
        /// tool loop converts exceptions into an <c>is_error</c> tool result fed back
        /// to the model, never a crashed stream.
        /// </summary>
        Task<ChatToolResult> ExecuteAsync(
            string toolName,
            string inputJson,
            IDictionary<string, object?> authContext,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// One Claude tool as the model sees it. <see cref="InputSchemaJson"/> is a JSON
    /// Schema object (<c>{"type":"object",...}</c>) kept as a JSON string so the
    /// connector contract stays free of provider SDK wire types.
    /// </summary>
    public sealed record ChatToolDefinition(string Name, string Description, string InputSchemaJson);

    /// <summary>
    /// The outcome of one tool execution. <see cref="TextPayload"/> is the JSON string
    /// returned to the model as the tool result (or the error text when
    /// <see cref="IsError"/>). Media references travel to transports (the chat
    /// middleware relays them as SSE <c>media</c> events); a vision image travels to
    /// the MODEL (the tool loop attaches it to the <c>tool_result</c> as a base64
    /// image block). Confirmation requests are carried for the plan slice that
    /// consumes them.
    /// </summary>
    public sealed record ChatToolResult
    {
        /// <summary>The tool result content fed back to the model (JSON string, or error text).</summary>
        public required string TextPayload { get; init; }

        /// <summary>Marks the result as a tool error (<c>is_error</c> on the wire); the model sees it and recovers.</summary>
        public bool IsError { get; init; }

        /// <summary>
        /// Media rows the result references (media connectors). The tool loop emits
        /// them as a <see cref="ChatToolMediaActivity"/> stream event after the
        /// result phase; they never enter the model conversation themselves.
        /// </summary>
        public IReadOnlyList<ChatToolMediaReference>? MediaReferences { get; init; }

        /// <summary>
        /// An image to attach to the <c>tool_result</c> content as a base64 vision
        /// block (media connectors with <c>chat-media-vision: enabled</c>). Null
        /// means the tool result is text-only and no bytes leave the server.
        /// </summary>
        public ChatToolVisionImage? VisionImage { get; init; }

        /// <summary>A gated write awaiting user confirmation (plan connectors; consumed by a later slice).</summary>
        public ChatToolConfirmationRequest? ConfirmationRequest { get; init; }
    }

    /// <summary>
    /// A media row a tool result points at, relayed to transports so clients can
    /// render the media alongside the answer. <see cref="MediaReference"/> is the
    /// client-usable reference: the stored URL for URL-mode bindings, or an opaque
    /// <c>bifrost-media://&lt;table&gt;/&lt;id&gt;</c> reference resolved by the
    /// auth-gated media fetch endpoint for binary-mode bindings — the reference
    /// carries no secret because the endpoint re-authorizes the row on every fetch.
    /// <see cref="ContentType"/> is set only when the server has seen the bytes
    /// (vision loads); binary fetches sniff it at request time.
    /// </summary>
    public sealed record ChatToolMediaReference(
        string Table, string Column, object RowId, string? ContentType, string MediaReference, string? Caption);

    /// <summary>
    /// Image bytes a tool result sends to the model as vision input.
    /// <see cref="MediaType"/> is the sniffed image media type (e.g.
    /// <c>image/png</c>); connectors must only attach recognized image formats.
    /// </summary>
    public sealed record ChatToolVisionImage(byte[] Data, string MediaType);

    /// <summary>A proposed gated write a plan tool wants the user to confirm (later slice).</summary>
    public sealed record ChatToolConfirmationRequest(string Kind, string Summary, string PayloadJson);

    /// <summary>
    /// A tool failure whose message is MODEL-VISIBLE BY DESIGN. Connectors throw this
    /// for validation feedback the model should read and act on (bad arguments, an
    /// unknown tool, a row it may not touch). Every other exception type is sanitized
    /// before it reaches the provider: the model sees only the tool name and the
    /// exception's TYPE name, never the raw message — exception messages routinely
    /// carry connection strings, table internals, and other server-side detail that
    /// must not leave the box. Author messages accordingly: this text goes off-box.
    /// </summary>
    public sealed class ChatToolInputException : Exception
    {
        public ChatToolInputException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Executes tool calls for ONE request: implementations are bound to a caller's
    /// auth context at creation (<see cref="ChatToolSet.CreateExecutor"/>), so the
    /// completion layer can run tools without ever holding an identity of its own.
    /// </summary>
    public interface IChatToolExecutor
    {
        /// <inheritdoc cref="IChatConnector.ExecuteAsync"/>
        Task<ChatToolResult> ExecuteAsync(string toolName, string inputJson, CancellationToken cancellationToken);
    }
}
