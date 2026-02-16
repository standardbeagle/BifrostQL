namespace BifrostQL.Core.QueryModel
{
    /// <summary>
    /// The type of operation a BifrostRequest represents.
    /// Maps to GraphQL operation types and their equivalents in other protocols.
    /// </summary>
    public enum BifrostRequestType
    {
        /// <summary>Read-only data retrieval (GraphQL query, OData GET, etc.).</summary>
        Query,
        /// <summary>Data modification (GraphQL mutation, OData POST/PATCH/DELETE, etc.).</summary>
        Mutation,
        /// <summary>Real-time data subscription (GraphQL subscription, SSE, etc.).</summary>
        Subscription,
    }

    /// <summary>
    /// Protocol-agnostic query intent that any frontend produces.
    /// Represents a single table-level operation with its fields, filter, and nested joins.
    ///
    /// GraphQL frontend: SqlVisitor AST -> IBifrostRequest.
    /// OData frontend: OData parser -> IBifrostRequest.
    /// Each frontend maps its query syntax to this common model.
    ///
    /// BifrostDispatcher accepts IBifrostRequest instead of protocol-specific AST.
    /// GqlObjectQuery is an implementation detail of the SQL pipeline, built from IBifrostRequest.
    /// </summary>
    public interface IBifrostRequest
    {
        /// <summary>
        /// The type of operation (Query, Mutation, or Subscription).
        /// </summary>
        BifrostRequestType RequestType { get; }

        /// <summary>
        /// The target table for this operation.
        /// Null when the request is a batch containing only nested joins.
        /// </summary>
        string? Table { get; }

        /// <summary>
        /// Optional alias for the result field (e.g., GraphQL field alias).
        /// </summary>
        string? Alias { get; }

        /// <summary>
        /// Filter criteria for the query, expressed as a protocol-agnostic filter tree.
        /// Null means no filtering (return all rows subject to other constraints).
        /// </summary>
        TableFilter? Filter { get; }

        /// <summary>
        /// Scalar fields to return from the table.
        /// Each string is a column name in the table's GraphQL schema.
        /// </summary>
        IReadOnlyList<string> Fields { get; }

        /// <summary>
        /// Named arguments for the operation (sort, limit, offset, operation-specific params).
        /// Values are protocol-agnostic: strings, numbers, booleans, lists, or nested dictionaries.
        /// </summary>
        IReadOnlyDictionary<string, object?> Arguments { get; }

        /// <summary>
        /// Nested join operations on related tables.
        /// Each join represents a sub-query on a linked table.
        /// </summary>
        IReadOnlyList<IBifrostRequest> Joins { get; }
    }

    /// <summary>
    /// Default implementation of IBifrostRequest.
    /// Constructed by protocol frontends after parsing their wire format.
    /// </summary>
    public sealed class BifrostQueryIntent : IBifrostRequest
    {
        public BifrostRequestType RequestType { get; init; }
        public string? Table { get; init; }
        public string? Alias { get; init; }
        public TableFilter? Filter { get; init; }
        public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, object?> Arguments { get; init; } = new Dictionary<string, object?>();
        public IReadOnlyList<IBifrostRequest> Joins { get; init; } = Array.Empty<IBifrostRequest>();
    }

    /// <summary>
    /// Converts between IQueryField (GraphQL-parsed) and IBifrostRequest (protocol-agnostic).
    /// This bridges the GraphQL frontend's SqlVisitor output into the common query model.
    /// </summary>
    public static class BifrostRequestAdapter
    {
        /// <summary>
        /// Converts a list of parsed GraphQL query fields into protocol-agnostic request intents.
        /// This is the GraphQL frontend's implementation of "AST -> IBifrostRequest".
        /// </summary>
        public static IReadOnlyList<IBifrostRequest> FromQueryFields(IEnumerable<IQueryField> fields, BifrostRequestType requestType)
        {
            return fields.Select(f => FromQueryField(f, requestType)).ToList();
        }

        /// <summary>
        /// Converts a single IQueryField tree into an IBifrostRequest tree.
        /// Recursively maps nested fields into the Joins and Fields properties.
        /// </summary>
        public static IBifrostRequest FromQueryField(IQueryField field, BifrostRequestType requestType)
        {
            var scalarFields = field.Fields
                .Where(f => f.Type == FieldType.Scalar)
                .Select(f => f.Alias ?? f.Name)
                .ToList();

            var joinFields = field.Fields
                .Where(f => f.Type is FieldType.Join or FieldType.Link)
                .Select(f => FromQueryField(f, requestType))
                .ToList();

            var arguments = new Dictionary<string, object?>();
            foreach (var arg in field.Arguments)
                arguments[arg.Name] = arg.Value;

            return new BifrostQueryIntent
            {
                RequestType = requestType,
                Table = field.Name,
                Alias = field.Alias,
                Filter = ExtractFilter(field.Arguments),
                Fields = scalarFields,
                Arguments = arguments,
                Joins = joinFields,
            };
        }

        /// <summary>
        /// Converts an IBifrostRequest back into an IQueryField for the SQL pipeline.
        /// This allows the existing GqlObjectQuery generation to work unchanged.
        /// </summary>
        public static IQueryField ToQueryField(IBifrostRequest request)
        {
            var fields = new List<IQueryField>();

            foreach (var fieldName in request.Fields)
            {
                fields.Add(new QueryField { Name = fieldName });
            }

            foreach (var join in request.Joins)
            {
                fields.Add(ToQueryField(join));
            }

            var arguments = new List<QueryArgument>();
            foreach (var arg in request.Arguments)
            {
                arguments.Add(new QueryArgument { Name = arg.Key, Value = arg.Value });
            }

            return new QueryField
            {
                Name = request.Table ?? "",
                Alias = request.Alias,
                Fields = fields,
                Arguments = arguments,
            };
        }

        /// <summary>
        /// Filter extraction from IQueryField is intentionally null.
        /// GraphQL filter arguments remain in the Arguments dictionary and are processed
        /// by QueryField.BuildCombinedFilter during ToSqlData(), which has the table context
        /// needed to parse filter dictionaries into TableFilter trees.
        /// Non-GraphQL frontends can set IBifrostRequest.Filter directly instead.
        /// </summary>
        private static TableFilter? ExtractFilter(List<QueryArgument> arguments) => null;
    }
}
