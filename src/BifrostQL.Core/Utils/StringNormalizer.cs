namespace BifrostQL.Core.Utils
{
    /// <summary>
    /// Provides centralized string normalization utilities to ensure consistent
    /// handling of database type names, column names, and other identifiers.
    /// </summary>
    public static class StringNormalizer
    {
        /// <summary>
        /// Normalizes a database type name for consistent comparison.
        /// Converts to lowercase and trims whitespace.
        /// </summary>
        /// <param name="type">The type name to normalize.</param>
        /// <returns>Normalized type name, or empty string if input is null.</returns>
        public static string NormalizeType(string? type) => 
            type?.ToLowerInvariant().Trim() ?? "";
        
        /// <summary>
        /// Normalizes a database column or table name for consistent comparison.
        /// Converts to lowercase and trims whitespace.
        /// </summary>
        /// <param name="name">The name to normalize.</param>
        /// <returns>Normalized name, or empty string if input is null.</returns>
        public static string NormalizeName(string? name) => 
            name?.ToLowerInvariant().Trim() ?? "";
        
        /// <summary>
        /// Normalizes a string for case-insensitive comparison.
        /// Converts to lowercase and trims whitespace.
        /// </summary>
        /// <param name="value">The string to normalize.</param>
        /// <returns>Normalized string, or empty string if input is null.</returns>
        public static string Normalize(string? value) => 
            value?.ToLowerInvariant().Trim() ?? "";
    }
}
