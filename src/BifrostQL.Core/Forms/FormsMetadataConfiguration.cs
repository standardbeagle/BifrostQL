namespace BifrostQL.Core.Forms
{
    /// <summary>
    /// Stores form control metadata for columns, keyed by "table.column".
    /// Provides a fluent API for configuring column-level form behavior.
    /// </summary>
    public sealed class FormsMetadataConfiguration
    {
        private readonly Dictionary<string, ColumnMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Configures form metadata for a specific column using the fluent API.
        /// The key format is "tableName.columnName" (case-insensitive).
        /// </summary>
        public FormsMetadataConfiguration ConfigureColumn(string tableName, string columnName, Action<ColumnMetadata> configure)
        {
            var key = $"{tableName}.{columnName}";
            if (!_metadata.TryGetValue(key, out var metadata))
            {
                metadata = new ColumnMetadata();
                _metadata[key] = metadata;
            }
            configure(metadata);
            return this;
        }

        /// <summary>
        /// Retrieves the metadata for a specific column, or null if none was configured.
        /// </summary>
        public ColumnMetadata? GetMetadata(string tableName, string columnName)
        {
            var key = $"{tableName}.{columnName}";
            return _metadata.TryGetValue(key, out var metadata) ? metadata : null;
        }
    }
}
