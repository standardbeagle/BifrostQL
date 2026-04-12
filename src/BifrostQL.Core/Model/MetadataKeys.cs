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
            /// <summary>Enables file storage for this column.</summary>
            public const string Storage = "file-storage";
            
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
        }
        
        /// <summary>
        /// Metadata keys for storage bucket configuration.
        /// </summary>
        public static class Storage
        {
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
            /// <summary>Auto-populate with current timestamp.</summary>
            public const string Timestamp = "timestamp";
            
            /// <summary>Auto-populate with current user.</summary>
            public const string User = "user";
            
            /// <summary>Auto-populate with UUID/GUID.</summary>
            public const string Guid = "guid";
        }
    }
}
