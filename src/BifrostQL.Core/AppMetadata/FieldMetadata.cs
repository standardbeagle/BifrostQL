namespace BifrostQL.Core.AppMetadata
{
    /// <summary>
    /// Field-level presentation and behavior metadata for the app-metadata
    /// overlay. This is a pure data type with no database or GraphQL
    /// dependencies; it describes how a single field of an entity should be
    /// rendered and validated by an application client (SPA or React Native).
    ///
    /// This overlay sits on top of — and does not replace — the existing
    /// BifrostQL schema-metadata system (<c>DbModel</c>, <c>MetadataKeys</c>,
    /// the <c>dbo.table { key: value }</c> rule grammar). The overlay is
    /// standalone JSON and deliberately does not reuse the <c>{ }</c> rule
    /// delimiter grammar.
    /// </summary>
    public sealed record FieldMetadata
    {
        /// <summary>
        /// The widget hint for rendering this field (e.g. "text", "select",
        /// "checkbox", "datepicker"). Null when no explicit widget is set.
        /// </summary>
        public string? Widget { get; init; }

        /// <summary>
        /// An opaque, client-interpreted validation expression or rule name.
        /// Null when no validation is configured. The overlay stores this
        /// verbatim and does not interpret it.
        /// </summary>
        public string? Validation { get; init; }

        /// <summary>
        /// Whether the field is visible in the application UI. Defaults to
        /// true so that an absent value renders the field.
        /// </summary>
        public bool Visible { get; init; } = true;

        /// <summary>
        /// Whether the field is read-only in the application UI. Defaults to
        /// false.
        /// </summary>
        public bool ReadOnly { get; init; }

        /// <summary>
        /// Help text shown alongside the field. Null when no help text is set.
        /// </summary>
        public string? HelpText { get; init; }

        /// <summary>
        /// The display group this field belongs to, used by clients to lay out
        /// related fields together. Null when the field is ungrouped.
        /// </summary>
        public string? Group { get; init; }
    }
}
