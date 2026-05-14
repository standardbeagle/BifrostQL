namespace BifrostQL.Core.AppMetadata
{
    /// <summary>
    /// Grid-preset presentation metadata for an entity in the app-metadata
    /// overlay. A grid preset describes how an application client should render
    /// the entity's list/table view: which columns appear by default, the
    /// default filters and sort, named saved views, and available bulk actions.
    ///
    /// This is a pure data type with no database or GraphQL dependency. The
    /// overlay sits on top of — and does not replace — the existing BifrostQL
    /// schema-metadata system; it is standalone JSON and deliberately does not
    /// reuse the <c>{ }</c> rule-delimiter grammar.
    /// </summary>
    public sealed record GridPresetMetadata
    {
        /// <summary>
        /// The ordered list of field names shown as columns by default. Empty
        /// when no explicit default columns are set; clients then choose their
        /// own default column set.
        /// </summary>
        public IReadOnlyList<string> DefaultColumns { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Opaque, client-interpreted default filter expressions applied when
        /// the grid first loads. The overlay stores these verbatim and does not
        /// interpret them. Empty when no default filters are set.
        /// </summary>
        public IReadOnlyList<string> DefaultFilters { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The ordered list of default sort directives (e.g.
        /// <c>created_at desc</c>). The overlay stores these verbatim. Empty
        /// when no default sort is set.
        /// </summary>
        public IReadOnlyList<string> DefaultSort { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Named saved views keyed by a stable identifier. Empty when the grid
        /// preset defines no saved views.
        /// </summary>
        public IReadOnlyDictionary<string, SavedViewMetadata> SavedViews { get; init; }
            = new Dictionary<string, SavedViewMetadata>();

        /// <summary>
        /// Opaque, client-interpreted bulk-action identifiers available from
        /// the grid (e.g. <c>delete</c>, <c>export</c>). The overlay stores
        /// these verbatim. Empty when no bulk actions are configured.
        /// </summary>
        public IReadOnlyList<string> BulkActions { get; init; } = Array.Empty<string>();
    }
}
