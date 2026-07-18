namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// The explicit depth/count/size bounds the List/Stream read compiler enforces on untrusted
    /// request input BEFORE it recurses or materializes anything (protocol-adapter-security
    /// invariant 6). Every value mirrors the OData read surface's caps so the two front doors bound
    /// the same shapes identically: a deeply-nested filter, a huge <c>and</c>/<c>or</c> tree, an
    /// oversized <c>_in</c> list, too many sort keys, or an over-long cursor all fail closed as a
    /// clean INVALID_ARGUMENT rather than an unbounded recursion/allocation.
    /// </summary>
    internal static class GrpcReadCaps
    {
        /// <summary>Maximum logical nesting depth of the filter <c>and</c>/<c>or</c> tree (OData MaxDepth).</summary>
        public const int MaxFilterDepth = 32;

        /// <summary>Maximum total number of leaf predicates + combiners in one filter (OData MaxTokens).</summary>
        public const int MaxFilterPredicates = 500;

        /// <summary>Maximum number of values in a single <c>_in</c>/<c>_between</c> list (OData MaxInListSize).</summary>
        public const int MaxInListSize = 200;

        /// <summary>Maximum number of caller-supplied sort keys (before the primary-key tiebreak).</summary>
        public const int MaxSortKeys = 32;

        /// <summary>Maximum length of the opaque page-token string the adapter will even attempt to decode.</summary>
        public const int MaxCursorChars = 4096;
    }
}
