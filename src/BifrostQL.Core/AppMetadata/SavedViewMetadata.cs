namespace BifrostQL.Core.AppMetadata
{
    /// <summary>
    /// A named, reusable view definition within a grid preset for the
    /// app-metadata overlay. A saved view captures a particular arrangement of
    /// columns, filters, and sort order that an application client can offer to
    /// users as a one-click selection.
    ///
    /// This is a pure data type with no database or GraphQL dependency. The
    /// overlay sits on top of — and does not replace — the existing BifrostQL
    /// schema-metadata system; it is standalone JSON.
    /// </summary>
    public sealed record SavedViewMetadata
    {
        /// <summary>
        /// The human-readable name of the saved view (e.g. "Active only").
        /// Null when the view is unnamed; clients should then fall back to the
        /// dictionary key under which the view is stored.
        /// </summary>
        public string? Name { get; init; }

        /// <summary>
        /// The ordered list of field names shown as columns in this view.
        /// Empty when the view inherits the grid preset's default columns.
        /// </summary>
        public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Opaque, client-interpreted filter expressions applied by this view.
        /// The overlay stores these verbatim and does not interpret them. Empty
        /// when the view applies no filters.
        /// </summary>
        public IReadOnlyList<string> Filters { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The ordered list of sort directives for this view (e.g.
        /// <c>created_at desc</c>). The overlay stores these verbatim. Empty
        /// when the view inherits the grid preset's default sort.
        /// </summary>
        public IReadOnlyList<string> Sort { get; init; } = Array.Empty<string>();
    }
}
