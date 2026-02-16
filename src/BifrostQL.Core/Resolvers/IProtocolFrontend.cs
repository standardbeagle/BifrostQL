namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Protocol-independent request produced by an IProtocolFrontend from wire-format input.
    /// Contains everything the Bifrost execution engine needs to process a query.
    /// </summary>
    public sealed class BifrostRequest
    {
        /// <summary>
        /// The query text in the frontend's native format (GraphQL query string, OData URL, etc.).
        /// The execution engine interprets this based on the originating frontend.
        /// </summary>
        public string Query { get; init; } = "";

        /// <summary>
        /// Named operation to execute when the query contains multiple operations.
        /// Null when the query contains a single operation.
        /// </summary>
        public string? OperationName { get; init; }

        /// <summary>
        /// Query variables/parameters provided by the client.
        /// </summary>
        public IReadOnlyDictionary<string, object?>? Variables { get; init; }

        /// <summary>
        /// Protocol-specific extensions passed through to the execution engine.
        /// GraphQL uses this for persisted queries, APQ hashes, etc.
        /// </summary>
        public IReadOnlyDictionary<string, object?>? Extensions { get; init; }

        /// <summary>
        /// Authenticated user context (claims, tenant ID, etc.).
        /// Built by the hosting layer from the transport's auth mechanism.
        /// </summary>
        public IDictionary<string, object?> UserContext { get; init; } = new Dictionary<string, object?>();

        /// <summary>
        /// DI service provider scoped to the current request.
        /// </summary>
        public IServiceProvider? RequestServices { get; init; }

        /// <summary>
        /// Cancellation token for cooperative cancellation of the request.
        /// </summary>
        public CancellationToken CancellationToken { get; init; }
    }

    /// <summary>
    /// Protocol-independent result returned by the Bifrost execution engine.
    /// The IProtocolFrontend serializes this into its wire format.
    /// </summary>
    public sealed class BifrostResult
    {
        /// <summary>
        /// The query result data. Structure depends on the query type:
        /// for GraphQL, this is the "data" field of the response.
        /// </summary>
        public object? Data { get; init; }

        /// <summary>
        /// Errors encountered during execution.
        /// Empty collection indicates success.
        /// </summary>
        public IReadOnlyList<BifrostResultError> Errors { get; init; } = Array.Empty<BifrostResultError>();

        /// <summary>
        /// Whether the execution completed successfully (no errors).
        /// </summary>
        public bool IsSuccess => Errors.Count == 0;
    }

    /// <summary>
    /// A single error from query execution, independent of any protocol's error format.
    /// </summary>
    public sealed class BifrostResultError
    {
        /// <summary>
        /// Human-readable error message.
        /// </summary>
        public string Message { get; init; } = "";

        /// <summary>
        /// The field path where the error occurred, if applicable.
        /// </summary>
        public IReadOnlyList<object>? Path { get; init; }

        /// <summary>
        /// Additional structured error metadata (error codes, hints, etc.).
        /// </summary>
        public IReadOnlyDictionary<string, object?>? Extensions { get; init; }
    }

    /// <summary>
    /// Contract for pluggable protocol frontends. Each protocol (GraphQL, OData, Protobuf, etc.)
    /// implements this to translate between its wire format and BifrostQL's internal representation.
    ///
    /// Implementations should be stateless; all request state flows through BifrostRequest/BifrostResult.
    ///
    /// Success metric: a new protocol frontend should be implementable in under 200 lines.
    /// </summary>
    public interface IProtocolFrontend
    {
        /// <summary>
        /// Unique identifier for this protocol (e.g., "graphql", "odata", "protobuf").
        /// Used for registration, logging, and routing.
        /// </summary>
        string ProtocolName { get; }

        /// <summary>
        /// The Content-Type this frontend handles on incoming requests (e.g., "application/json", "application/protobuf").
        /// Used by the routing layer to dispatch requests to the correct frontend.
        /// </summary>
        string ContentType { get; }

        /// <summary>
        /// Parses a protocol-specific request body into a BifrostRequest.
        /// The stream contains the raw request body; the frontend reads and interprets it.
        /// </summary>
        /// <param name="body">The raw request body stream.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The parsed request, or null if the body is empty/invalid.</returns>
        ValueTask<BifrostRequest?> ParseAsync(Stream body, CancellationToken cancellationToken);

        /// <summary>
        /// Serializes a BifrostResult into the protocol's wire format and writes it to the output stream.
        /// </summary>
        /// <param name="output">The output stream to write the serialized response to.</param>
        /// <param name="result">The execution result to serialize.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        ValueTask SerializeAsync(Stream output, BifrostResult result, CancellationToken cancellationToken);

        /// <summary>
        /// The Content-Type to set on the response for this protocol's output format.
        /// May differ from the request ContentType (e.g., a protocol might accept JSON but respond with protobuf).
        /// </summary>
        string ResponseContentType { get; }
    }

    /// <summary>
    /// Executes BifrostRequests against the database model and schema.
    /// This is the protocol-independent execution engine that all frontends delegate to.
    /// </summary>
    public interface IBifrostEngine
    {
        /// <summary>
        /// Executes a BifrostRequest and returns a BifrostResult.
        /// Handles schema lookup, query transformation, SQL generation, and execution.
        /// </summary>
        /// <param name="request">The protocol-independent request to execute.</param>
        /// <param name="endpointPath">The endpoint path for schema/model resolution from PathCache.</param>
        /// <returns>The execution result.</returns>
        Task<BifrostResult> ExecuteAsync(BifrostRequest request, string endpointPath);
    }
}
