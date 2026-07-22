namespace BifrostQL.Server.Feeds
{
    /// <summary>
    /// The format-neutral projection of a feed: channel/feed-level metadata plus the ordered items.
    /// It is the single value both the RSS 2.0 and Atom 1.0 writers serialize, so the two formats
    /// render identical data and only their envelope differs. Every string here is treated as
    /// untrusted at serialization time (item values originate from database rows) — the writers XML-
    /// escape all of them; nothing here is pre-escaped.
    /// </summary>
    public sealed class FeedDocument
    {
        /// <summary>Channel/feed title. Operator-supplied.</summary>
        public required string Title { get; init; }

        /// <summary>The feed's self/site link. Operator-supplied.</summary>
        public required string Link { get; init; }

        /// <summary>
        /// The feed-level author name (Atom <c>&lt;author&gt;&lt;name&gt;</c>). RFC 4287 §4.1.1 requires
        /// an author at the feed level (or on every entry); the writer emits it at the feed level.
        /// Operator-supplied trusted text — the writer still XML-escapes it.
        /// </summary>
        public required string Author { get; init; }

        /// <summary>Channel description (RSS). May be null for Atom-only use.</summary>
        public string? Description { get; init; }

        /// <summary>
        /// The stable feed identity (Atom <c>&lt;id&gt;</c>). Derived from <see cref="Link"/> so it is
        /// constant for a given surface across scrapes.
        /// </summary>
        public required string FeedId { get; init; }

        /// <summary>
        /// The most recent item timestamp (UTC), used for Atom's required feed <c>&lt;updated&gt;</c>.
        /// Null only when the feed is empty.
        /// </summary>
        public DateTime? Updated { get; init; }

        /// <summary>The feed items, already ordered newest-first by the planner.</summary>
        public IReadOnlyList<FeedItem> Items { get; init; } = Array.Empty<FeedItem>();
    }

    /// <summary>
    /// One feed item. <see cref="Guid"/> is deterministic from the row's complete primary key plus its
    /// configured timestamp (see <c>FeedReadPlanner</c>), so re-reading an unchanged row yields the
    /// same identifier in both formats. All text fields carry raw row values; the writers escape them.
    /// </summary>
    public sealed class FeedItem
    {
        /// <summary>Stable, deterministic item identifier (RSS <c>&lt;guid&gt;</c>, Atom <c>&lt;id&gt;</c>).</summary>
        public required string Guid { get; init; }

        /// <summary>The item title, from the expanded feed-title template.</summary>
        public required string Title { get; init; }

        /// <summary>The item body/content, from the configured body column. May be null.</summary>
        public string? Body { get; init; }

        /// <summary>The item link, from the expanded feed-link template. May be null when unconfigured.</summary>
        public string? Link { get; init; }

        /// <summary>The item timestamp (UTC), from the configured timestamp column.</summary>
        public required DateTime Timestamp { get; init; }
    }
}
