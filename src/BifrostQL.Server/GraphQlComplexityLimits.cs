using GraphQL.Validation.Complexity;
using Microsoft.Extensions.Configuration;

namespace BifrostQL.Server
{
    /// <summary>
    /// Bounded GraphQL depth/complexity limits shared by the single-database and
    /// multi-database setup paths. BifrostQL translates nested joins and aggregates into
    /// correlated subqueries, so an unbounded, deeply nested operation can amplify one
    /// request into an enormous SQL workload — an unauthenticated denial-of-service
    /// vector. These limits are applied to the shared GraphQL executor (which the binary
    /// WebSocket transport also uses) via GraphQL.NET's complexity analyzer.
    /// </summary>
    internal static class GraphQlComplexityLimits
    {
        /// <summary>
        /// Default maximum selection-set nesting depth. Applied whenever the host does not
        /// configure <c>MaxQueryDepth</c>. Chosen to comfortably exceed legitimate nested
        /// join/aggregate queries while rejecting pathological nesting.
        /// </summary>
        public const int DefaultMaxDepth = 20;

        /// <summary>
        /// Reads <c>MaxQueryDepth</c> and <c>MaxQueryComplexity</c> from a configuration
        /// section. A missing or non-positive depth falls back to <see cref="DefaultMaxDepth"/>.
        /// Complexity is optional: null (or non-positive) leaves the field-count limit off.
        /// A depth of 0 or a negative value in config is treated as "use the default" rather
        /// than "disable the guard", so the limit cannot be silently turned off by mistake.
        /// </summary>
        public static (int maxDepth, int? maxComplexity) Read(IConfigurationSection? section)
        {
            var configuredDepth = section?.GetValue<int?>("MaxQueryDepth");
            var maxDepth = configuredDepth is > 0 ? configuredDepth.Value : DefaultMaxDepth;

            var configuredComplexity = section?.GetValue<int?>("MaxQueryComplexity");
            var maxComplexity = configuredComplexity is > 0 ? configuredComplexity : null;

            return (maxDepth, maxComplexity);
        }

        /// <summary>
        /// Applies the resolved limits to GraphQL.NET's <see cref="ComplexityOptions"/>.
        /// </summary>
        public static void Apply(ComplexityOptions options, int maxDepth, int? maxComplexity)
        {
            options.MaxDepth = maxDepth;
            if (maxComplexity.HasValue)
                options.MaxComplexity = maxComplexity.Value;
        }
    }
}
