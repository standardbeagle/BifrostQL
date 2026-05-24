using System.Text.Json;
using BifrostQL.Core.QueryModel.VisualQuery;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// Parsing helpers shared by the <c>build-sql</c> and <c>build-and-exec</c>
    /// bridge handlers.
    ///
    /// System.Text.Json materializes the <see cref="VisualCriterion.Value"/>
    /// <c>object?</c> field as a <see cref="JsonElement"/>. <see cref="VisualQueryBuilder"/>
    /// expects CLR scalars (and CLR arrays for <c>_in</c>/<c>_between</c>) and the
    /// values must also bind as <c>DbParameter</c>s downstream, so this normalizes
    /// every criterion value from JsonElement into CLR before building.
    /// </summary>
    public static class VisualQueryBridge
    {
        private static readonly JsonSerializerOptions SpecJson = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>Deserializes and normalizes a <see cref="VisualQuerySpec"/> from a bridge payload.</summary>
        public static VisualQuerySpec Parse(JsonElement payload)
        {
            if (payload.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Visual query payload must be an object.");

            var spec = payload.Deserialize<VisualQuerySpec>(SpecJson)
                ?? throw new ArgumentException("Invalid visual query spec.");

            return spec with { Filter = NormalizeFilter(spec.Filter) };
        }

        private static VisualFilter? NormalizeFilter(VisualFilter? node)
        {
            if (node is null) return null;

            IReadOnlyList<VisualFilter>? children = node.Children is null
                ? null
                : node.Children.Select(NormalizeFilter).OfType<VisualFilter>().ToList();

            var criterion = node.Criterion is null
                ? null
                : node.Criterion with { Value = JsonValueToClr(node.Criterion.Value) };

            return node with { Children = children, Criterion = criterion };
        }

        /// <summary>Converts a JsonElement (or passes through an already-CLR value) to a CLR value.</summary>
        public static object? JsonValueToClr(object? value)
        {
            if (value is not JsonElement e)
                return value;

            return e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                // Cast the long branch to object so the conditional's type is
                // object, not double — otherwise the long is widened to double
                // and every integer would bind as a float.
                JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.Array => e.EnumerateArray().Select(item => JsonValueToClr(item)).ToArray(),
                _ => e.GetRawText(),
            };
        }
    }
}
