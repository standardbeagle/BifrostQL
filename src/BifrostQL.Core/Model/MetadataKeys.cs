namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Centralized constants for metadata key names used throughout BifrostQL.
    /// Using these constants prevents typos and enables easier refactoring.
    /// </summary>
    public static class MetadataKeys
    {
        /// <summary>
        /// Metadata keys for EAV (Entity-Attribute-Value) configuration.
        /// </summary>
        public static class Eav
        {
            /// <summary>The parent table name for an EAV meta table.</summary>
            public const string Parent = "eav-parent";

            /// <summary>The foreign key column linking to the parent table.</summary>
            public const string ForeignKey = "eav-fk";

            /// <summary>The column containing the attribute/key name.</summary>
            public const string Key = "eav-key";

            /// <summary>The column containing the attribute value.</summary>
            public const string Value = "eav-value";
        }

        /// <summary>
        /// Metadata keys for file storage configuration.
        /// </summary>
        public static class FileStorage
        {
            /// <summary>Enables file storage for this column (legacy alias).</summary>
            public const string Storage = "file-storage";

            /// <summary>
            /// Column-level tag marking a column as a file column. Carries an optional
            /// inline configuration blob parsed by <c>FileColumnConfig.FromMetadata</c>.
            /// </summary>
            public const string File = "file";

            /// <summary>Maximum file size in bytes.</summary>
            public const string MaxSize = "max-size";

            /// <summary>Column containing the MIME type.</summary>
            public const string ContentTypeColumn = "content-type-column";

            /// <summary>Column containing the original filename.</summary>
            public const string FileNameColumn = "file-name-column";

            /// <summary>Accepted file types (MIME type pattern).</summary>
            public const string Accept = "accept";
        }

        /// <summary>
        /// Metadata keys for data type and format hints.
        /// </summary>
        public static class DataType
        {
            /// <summary>Override the detected data type.</summary>
            public const string Type = "type";

            /// <summary>Data format hint (e.g., "php", "json", "xml").</summary>
            public const string Format = "format";

            /// <summary>Indicates PHP serialized data.</summary>
            public const string PhpSerialized = "php_serialized";

            /// <summary>Default value for a column.</summary>
            public const string Default = "default";

            /// <summary>Display title; also serves as a fallback for pattern-message.</summary>
            public const string Title = "title";
        }

        /// <summary>
        /// Metadata keys for storage bucket configuration.
        /// </summary>
        public static class Storage
        {
            /// <summary>
            /// Top-level metadata key carrying a storage bucket configuration blob
            /// at the column, table, or model level. Parsed by
            /// <c>StorageBucketConfig.FromMetadata</c>. Distinct from
            /// <see cref="FileStorage.Storage"/>, which is a legacy alias.
            /// </summary>
            public const string Config = "storage";

            /// <summary>Storage bucket name.</summary>
            public const string Bucket = "bucket";

            /// <summary>Storage provider type (local, s3, etc.).</summary>
            public const string Provider = "provider";

            /// <summary>Path prefix for stored files.</summary>
            public const string Prefix = "prefix";

            /// <summary>Base path for local storage.</summary>
            public const string BasePath = "basePath";
        }

        /// <summary>
        /// Metadata keys for UI and display configuration.
        /// </summary>
        public static class Ui
        {
            /// <summary>Column label for display.</summary>
            public const string Label = "label";

            /// <summary>Hide this table or column from the UI.</summary>
            public const string Hidden = "hidden";

            /// <summary>Mark as read-only.</summary>
            public const string ReadOnly = "readonly";
        }

        /// <summary>
        /// Metadata keys for validation configuration.
        /// </summary>
        public static class Validation
        {
            /// <summary>Minimum value for numeric types.</summary>
            public const string Min = "min";

            /// <summary>Maximum value for numeric types.</summary>
            public const string Max = "max";

            /// <summary>Step value for numeric inputs.</summary>
            public const string Step = "step";

            /// <summary>Minimum length for strings.</summary>
            public const string MinLength = "minlength";

            /// <summary>Maximum length for strings.</summary>
            public const string MaxLength = "maxlength";

            /// <summary>Regex pattern for validation.</summary>
            public const string Pattern = "pattern";

            /// <summary>Error message for pattern validation.</summary>
            public const string PatternMessage = "pattern-message";

            /// <summary>HTML5 input type override.</summary>
            public const string InputType = "input-type";

            /// <summary>Whether the field is required.</summary>
            public const string Required = "required";
        }

        /// <summary>
        /// Metadata keys for enum configuration.
        /// </summary>
        public static class Enum
        {
            /// <summary>Comma-separated list of enum values.</summary>
            public const string Values = "enum-values";

            /// <summary>Comma-separated list of display labels for enum values.</summary>
            public const string Labels = "enum-labels";
        }

        /// <summary>
        /// Metadata keys for auto-population.
        /// </summary>
        public static class AutoPopulate
        {
            /// <summary>
            /// Column-level marker tagging a column for auto-population by an audit
            /// or system module. The value names the populator (e.g. "created-on",
            /// "updated-by"). When set, the field is excluded from form rendering
            /// and treated as read-only.
            /// </summary>
            public const string Marker = "populate";

            /// <summary>Auto-populate with current timestamp.</summary>
            public const string Timestamp = "timestamp";

            /// <summary>Auto-populate with current user.</summary>
            public const string User = "user";

            /// <summary>Auto-populate with UUID/GUID.</summary>
            public const string Guid = "guid";
        }

        /// <summary>
        /// Metadata keys for audit-column population by <c>BasicAuditModule</c>.
        /// </summary>
        public static class Audit
        {
            /// <summary>
            /// Model-level metadata key naming the user-context claim used to
            /// populate created-by / updated-by / deleted-by columns.
            /// </summary>
            public const string UserKey = "user-audit-key";
        }

        /// <summary>
        /// Metadata keys for explicit relationship declarations.
        /// </summary>
        public static class Relationships
        {
            /// <summary>
            /// Table-level many-to-many declaration. Format:
            /// <c>"TargetTable:JunctionTable[, TargetTable:JunctionTable...]"</c>.
            /// </summary>
            public const string ManyToMany = "many-to-many";

            /// <summary>
            /// Model-level toggle for emitting dynamic <c>_join</c> / <c>_single</c>
            /// containers in the GraphQL schema. Defaults to true.
            /// </summary>
            public const string DynamicJoins = "dynamic-joins";
        }

        /// <summary>
        /// Metadata keys for batch mutation configuration.
        /// </summary>
        public static class Batch
        {
            /// <summary>Per-table override for the maximum batch size.</summary>
            public const string MaxSize = "batch-max-size";
        }

        /// <summary>
        /// Metadata keys for the raw SQL query feature.
        /// </summary>
        public static class RawSql
        {
            /// <summary>
            /// Model-level toggle that enables the <c>_rawQuery</c> field. The
            /// value must be <c>"enabled"</c> for raw SQL queries to be exposed.
            /// </summary>
            public const string Enabled = "raw-sql";
        }
    }
}
