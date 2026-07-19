using System.Text.Json;
using System.Text.Json.Serialization;

namespace BifrostQL.Mcp
{
    public sealed record DeclarativeToolDocument
    {
        public int Version { get; init; }
        public IReadOnlyList<DeclarativeToolDefinition> Tools { get; init; } = [];
    }

    public sealed record DeclarativeToolDefinition
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public IReadOnlyDictionary<string, DeclarativeToolParameter> Params { get; init; }
            = new Dictionary<string, DeclarativeToolParameter>();
        public DeclarativeToolRoot Root { get; init; } = new();
        public IReadOnlyList<DeclarativeToolInclude> Include { get; init; } = [];
        public DeclarativeToolPolicy Policy { get; init; } = new();

        /// <summary>
        /// When set, this is a declared WRITE tool (off by default, gated by
        /// EnableWrites) rather than a read tool. A tool declares EITHER
        /// <see cref="Root"/> (read) OR <see cref="Mutation"/> (write), never both.
        /// </summary>
        public DeclarativeToolMutation? Mutation { get; init; }

        public bool IsMutation => Mutation is not null;
    }

    /// <summary>
    /// A declared write tool: maps the tool's parameters (and fixed/default literals)
    /// onto a single <c>MutationIntent</c> — insert values, an update SET, or a
    /// delete addressed by a positional primary key. The tool builds NO predicate of
    /// its own; scope narrowing comes from the pipeline via the caller's identity.
    /// </summary>
    public sealed record DeclarativeToolMutation
    {
        /// <summary>Schema-qualified target table, e.g. <c>.Tickets</c> / <c>dbo.orders</c>.</summary>
        public string Table { get; init; } = string.Empty;

        /// <summary><c>insert</c>, <c>update</c>, or <c>delete</c>.</summary>
        public string Action { get; init; } = string.Empty;

        /// <summary>
        /// Column-value map for insert/update. A string value of the form
        /// <c>$paramName</c> binds the named parameter's call-time value; any other
        /// JSON value is a FIXED literal. A fixed literal for a security-pinned column
        /// (e.g. a tenant id) is still overridden by the pipeline's transformer — it
        /// can never widen scope.
        /// </summary>
        public IReadOnlyDictionary<string, JsonElement> Values { get; init; }
            = new Dictionary<string, JsonElement>();

        /// <summary>
        /// For update/delete: the parameter (type <c>id</c>) carrying the positional
        /// primary key of the row to write. The pipeline ANDs the caller's scope onto
        /// it, so an out-of-scope key affects zero rows.
        /// </summary>
        public string? ById { get; init; }
    }

    public sealed record DeclarativeToolParameter
    {
        public string Type { get; init; } = string.Empty;
        public string? Table { get; init; }
        public string? Description { get; init; }
        public IReadOnlyList<string>? Values { get; init; }
        public JsonElement? Default { get; init; }
    }

    public sealed record DeclarativeToolRoot
    {
        public string Table { get; init; } = string.Empty;
        public string ById { get; init; } = string.Empty;
        public IReadOnlyList<string> Fields { get; init; } = [];
    }

    public sealed record DeclarativeToolInclude
    {
        public string Relation { get; init; } = string.Empty;
        public string As { get; init; } = string.Empty;
        public JsonElement? Filter { get; init; }
        public IReadOnlyList<string>? Fields { get; init; }
        public string? Sort { get; init; }
        public int? Limit { get; init; }
        public DeclarativeToolAggregate? Aggregate { get; init; }
        public string? DetailGate { get; init; }
    }

    public sealed record DeclarativeToolAggregate
    {
        public bool Count { get; init; }
        public string? Sum { get; init; }
        public string? Avg { get; init; }
        public string? Min { get; init; }
        public string? Max { get; init; }
    }

    public sealed record DeclarativeToolPolicy
    {
        public string HiddenFieldBehavior { get; init; } = "omit";
        public IReadOnlyList<string>? AllowedRoles { get; init; }
    }

    internal static class DeclarativeToolJson
    {
        public static JsonSerializerOptions Options { get; } = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
    }
}
