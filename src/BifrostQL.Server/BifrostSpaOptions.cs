namespace BifrostQL.Server
{
    /// <summary>
    /// Configuration for <see cref="SpaHostingExtensions.UseBifrostSpa"/>.
    /// Controls where SPA assets are served from and which request paths are excluded
    /// from the <c>index.html</c> route fallback.
    /// </summary>
    public sealed class BifrostSpaOptions
    {
        /// <summary>
        /// Directory containing the built SPA assets (the folder holding <c>index.html</c>).
        /// When null or empty, the host's default web root (<c>wwwroot</c>) is used.
        /// </summary>
        public string? AssetDirectory { get; set; }

        /// <summary>
        /// Path prefixes that bypass the SPA <c>index.html</c> fallback so that GraphQL,
        /// the GraphQL playground, health checks, and API routes are not shadowed.
        /// Defaults to <c>/graphql</c>, <c>/api</c>, and <c>/health</c>.
        /// </summary>
        public IReadOnlyCollection<string> ExcludedPathPrefixes { get; set; }
            = new[] { "/graphql", "/api", "/health" };

        /// <summary>
        /// Adds a path prefix to <see cref="ExcludedPathPrefixes"/>.
        /// Use this to exclude a non-default GraphQL endpoint or playground path.
        /// </summary>
        public BifrostSpaOptions AddExcludedPathPrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                throw new ArgumentException("Prefix must not be empty.", nameof(prefix));

            ExcludedPathPrefixes = new List<string>(ExcludedPathPrefixes) { prefix };
            return this;
        }
    }
}
