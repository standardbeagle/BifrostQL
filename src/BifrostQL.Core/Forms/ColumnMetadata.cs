namespace BifrostQL.Core.Forms
{
    /// <summary>
    /// Stores per-column metadata that customizes form control generation.
    /// All properties are optional; unset values fall back to automatic type mapping.
    /// </summary>
    public sealed class ColumnMetadata
    {
        /// <summary>
        /// Overrides the HTML input type (e.g., "email", "tel", "url").
        /// When set, takes priority over automatic type mapping from the database type.
        /// </summary>
        public string? InputType { get; set; }

        /// <summary>
        /// Placeholder text shown in the input when empty.
        /// </summary>
        public string? Placeholder { get; set; }

        /// <summary>
        /// HTML5 pattern attribute for client-side regex validation.
        /// </summary>
        public string? Pattern { get; set; }

        /// <summary>
        /// Minimum value for numeric inputs or minimum length for text inputs.
        /// </summary>
        public double? Min { get; set; }

        /// <summary>
        /// Maximum value for numeric inputs or maximum length for text inputs.
        /// </summary>
        public double? Max { get; set; }

        /// <summary>
        /// Step value for numeric inputs (e.g., 0.01 for currency).
        /// When set, overrides the automatic step from type mapping.
        /// </summary>
        public double? Step { get; set; }

        /// <summary>
        /// When set, the column renders as an enum control (radio buttons or select)
        /// instead of a text input. Values are the raw enum option values.
        /// </summary>
        public string[]? EnumValues { get; set; }

        /// <summary>
        /// Display names for enum values. Keys must match entries in <see cref="EnumValues"/>.
        /// When a value has no display name, it is used as-is.
        /// </summary>
        public IReadOnlyDictionary<string, string>? EnumDisplayNames { get; set; }

        /// <summary>
        /// Accept attribute for file inputs (e.g., "image/png,image/jpeg").
        /// Only applies when the column renders as a file input.
        /// </summary>
        public string? Accept { get; set; }

        /// <summary>
        /// Minimum character length for text inputs (HTML5 minlength attribute).
        /// </summary>
        public int? MinLength { get; set; }

        /// <summary>
        /// Maximum character length for text inputs (HTML5 maxlength attribute).
        /// </summary>
        public int? MaxLength { get; set; }

        /// <summary>
        /// When true, the field is required regardless of the database column's nullability.
        /// When false, the field is optional even if the database column is NOT NULL.
        /// When null, the field falls back to the schema-derived required state.
        /// </summary>
        public bool? Required { get; set; }

        /// <summary>
        /// HTML title attribute, used as the pattern validation hint message
        /// in browsers when a pattern mismatch occurs.
        /// </summary>
        public string? Title { get; set; }
    }
}
