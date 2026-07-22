namespace BifrostQL.Server.Feeds
{
    /// <summary>
    /// Server-side configuration for a syndication feed surface. The presentation fields
    /// (<see cref="Title"/>/<see cref="Link"/>/<see cref="Description"/>) are operator-supplied
    /// channel/feed metadata — never row data — so they are trusted text the writers still escape.
    /// The bounds (<see cref="MaxItems"/>/<see cref="DefaultItems"/>) are the server-side ceiling and
    /// fallback a caller's requested <c>limit</c> is clamped to, so an unbounded or oversized request
    /// can never widen the page (.claude/rules/protocol-adapter-security.md — an adapter's declared
    /// bound is the only limit it may set).
    /// </summary>
    public sealed class FeedOptions
    {
        /// <summary>The server-side maximum number of items any single feed request may return.</summary>
        public int MaxItems { get; init; } = 50;

        /// <summary>The page size used when a caller supplies no <c>limit</c>. Clamped to <see cref="MaxItems"/>.</summary>
        public int DefaultItems { get; init; } = 20;

        /// <summary>The channel/feed title (RSS <c>&lt;title&gt;</c>, Atom <c>&lt;title&gt;</c>).</summary>
        public required string Title { get; init; }

        /// <summary>The feed's self/site URL (RSS <c>&lt;link&gt;</c>, Atom self link + <c>&lt;id&gt;</c>).</summary>
        public required string Link { get; init; }

        /// <summary>The channel description (RSS requires it; Atom has no equivalent required field).</summary>
        public string? Description { get; init; }
    }
}
