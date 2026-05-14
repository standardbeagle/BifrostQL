namespace BifrostQL.Core.AppMetadata
{
    /// <summary>
    /// Entity-level presentation metadata for the app-metadata overlay. An
    /// entity corresponds to a qualified table in the underlying schema, but
    /// this type carries no database or GraphQL dependency — it is pure data
    /// describing how an application client should present the entity and its
    /// fields.
    ///
    /// This overlay sits on top of — and does not replace — the existing
    /// BifrostQL schema-metadata system. The overlay is standalone JSON.
    /// </summary>
    public sealed record EntityMetadata
    {
        /// <summary>
        /// The human-readable label for the entity. Null when no explicit
        /// label is set; clients fall back to the qualified table name.
        /// </summary>
        public string? Label { get; init; }

        /// <summary>
        /// An icon hint for the entity (e.g. an icon name or token). Null when
        /// no icon is set.
        /// </summary>
        public string? Icon { get; init; }

        /// <summary>
        /// The field name(s) used to render a short display representation of
        /// a row (e.g. for pick lists). Empty when no display field is set.
        /// </summary>
        public IReadOnlyList<string> DisplayFields { get; init; } = Array.Empty<string>();

        /// <summary>
        /// A navigation placement hint identifying where the entity appears in
        /// the application's navigation (e.g. a section or menu group). Null
        /// when the entity has no explicit navigation placement.
        /// </summary>
        public string? NavPlacement { get; init; }

        /// <summary>
        /// Field-level metadata keyed by field name. Empty when no fields have
        /// overlay metadata.
        /// </summary>
        public IReadOnlyDictionary<string, FieldMetadata> Fields { get; init; }
            = new Dictionary<string, FieldMetadata>();

        /// <summary>
        /// The grid-preset metadata describing how the entity's list/table view
        /// should be presented. Null when the entity has no grid preset; clients
        /// then choose their own list presentation.
        /// </summary>
        public GridPresetMetadata? Grid { get; init; }

        /// <summary>
        /// Relationship metadata keyed by relationship name. Each entry
        /// references its target entity by qualified table name. Empty when the
        /// entity has no overlay relationship metadata.
        /// </summary>
        public IReadOnlyDictionary<string, RelationshipMetadata> Relationships { get; init; }
            = new Dictionary<string, RelationshipMetadata>();
    }
}
