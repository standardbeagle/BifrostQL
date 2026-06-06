namespace BifrostQL.Core.Model.Relationships
{
    /// <summary>
    /// Case-insensitive equality comparer for (schema, table-name) keys used when
    /// indexing model tables during relationship discovery.
    /// </summary>
    internal sealed class SchemaTableComparer : IEqualityComparer<(string Schema, string Name)>
    {
        public bool Equals((string Schema, string Name) x, (string Schema, string Name) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Schema, y.Schema) &&
               StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode((string Schema, string Name) obj)
            => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema) ^
               StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
    }
}
