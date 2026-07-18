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
