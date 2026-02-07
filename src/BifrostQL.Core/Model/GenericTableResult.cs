namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Metadata about a single column in a generic table query result.
    /// </summary>
    public sealed class GenericColumnMetadata
    {
        public string Name { get; init; } = null!;
        public string DataType { get; init; } = null!;
        public bool IsNullable { get; init; }
        public bool IsPrimaryKey { get; init; }
    }

    /// <summary>
    /// Result of a generic table query, including column metadata and rows as key-value pairs.
    /// </summary>
    public sealed class GenericTableResult
    {
        public string TableName { get; init; } = null!;
        public IReadOnlyList<GenericColumnMetadata> Columns { get; init; } = Array.Empty<GenericColumnMetadata>();
        public IReadOnlyList<Dictionary<string, object?>> Rows { get; init; } = Array.Empty<Dictionary<string, object?>>();
        public int TotalCount { get; init; }
    }
}
