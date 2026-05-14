namespace BifrostQL.Core.AppMetadata
{
    /// <summary>
    /// Relationship presentation metadata for an entity in the app-metadata
    /// overlay. Relationship metadata describes how an application client
    /// should render a relationship to another entity: as a foreign-key
    /// selector, as an embedded child collection, or as a nested detail panel.
    ///
    /// The related entity is identified by its qualified table name (e.g.
    /// <c>dbo.users</c>), consistent with how <see cref="AppMetadataModel"/>
    /// keys its entity dictionary.
    ///
    /// This is a pure data type with no database or GraphQL dependency. The
    /// overlay sits on top of — and does not replace — the existing BifrostQL
    /// schema-metadata system; it is standalone JSON.
    /// </summary>
    public sealed record RelationshipMetadata
    {
        /// <summary>
        /// The qualified table name of the related entity (e.g.
        /// <c>sales.orders</c>). Null when the relationship target is not yet
        /// resolved; clients should then treat the relationship as inert.
        /// </summary>
        public string? TargetEntity { get; init; }

        /// <summary>
        /// The kind of relationship presentation. Defaults to
        /// <see cref="RelationshipKind.ForeignKeySelector"/> so an absent value
        /// renders a single-value picker.
        /// </summary>
        public RelationshipKind Kind { get; init; } = RelationshipKind.ForeignKeySelector;

        /// <summary>
        /// The field name on this entity (for a foreign-key selector) or on the
        /// target entity (for a child collection) that carries the foreign key.
        /// Null when the foreign-key field is implied by the schema.
        /// </summary>
        public string? ForeignKeyField { get; init; }

        /// <summary>
        /// The ordered list of target-entity field names to show when the
        /// relationship is rendered as a child collection or nested panel.
        /// Empty when the client should fall back to the target entity's own
        /// display fields.
        /// </summary>
        public IReadOnlyList<string> DisplayColumns { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The human-readable label for the relationship as shown on this
        /// entity (e.g. "Orders"). Null when no explicit label is set.
        /// </summary>
        public string? Label { get; init; }
    }

    /// <summary>
    /// The presentation kind for a <see cref="RelationshipMetadata"/> entry.
    /// Serialized as a camelCase string in the overlay JSON contract.
    /// </summary>
    public enum RelationshipKind
    {
        /// <summary>
        /// A single-value foreign-key picker (e.g. a dropdown selecting one
        /// related row).
        /// </summary>
        ForeignKeySelector,

        /// <summary>
        /// An embedded collection of related child rows shown inline.
        /// </summary>
        ChildCollection,

        /// <summary>
        /// A nested detail panel rendering the related entity's own layout.
        /// </summary>
        NestedPanel,
    }
}
