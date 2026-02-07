using System.Text.RegularExpressions;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Validates that raw SQL queries contain only SELECT statements.
    /// Rejects any data modification (INSERT, UPDATE, DELETE, MERGE),
    /// schema modification (CREATE, ALTER, DROP, TRUNCATE), or
    /// execution statements (EXEC, EXECUTE, xp_, sp_).
    /// </summary>
    public sealed class RawSqlValidator
    {
        // Matches dangerous keywords at word boundaries, case-insensitive.
        // These must not appear as standalone statements in the SQL.
        private static readonly Regex ForbiddenKeywords = new(
            @"\b(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC|EXECUTE|GRANT|REVOKE|DENY|BACKUP|RESTORE|SHUTDOWN|DBCC|BULK|OPENROWSET|OPENQUERY|OPENDATASOURCE|INTO)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches SQL comments that could hide malicious code
        private static readonly Regex BlockComment = new(
            @"/\*.*?\*/",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex LineComment = new(
            @"--.*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        // Matches semicolons outside of string literals (multiple-statement detection)
        private static readonly Regex SemicolonOutsideStrings = new(
            @";(?=(?:[^']*'[^']*')*[^']*$)",
            RegexOptions.Compiled);

        /// <summary>
        /// Validates that the SQL is a single SELECT statement with no dangerous operations.
        /// </summary>
        /// <returns>A validation result with success/failure and error message.</returns>
        public RawSqlValidationResult Validate(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return RawSqlValidationResult.Fail("SQL query must not be empty.");

            // Strip comments to prevent keyword hiding
            var stripped = BlockComment.Replace(sql, " ");
            stripped = LineComment.Replace(stripped, " ");

            // Check for multiple statements via semicolons
            var statements = SemicolonOutsideStrings.Split(stripped)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (statements.Length > 1)
                return RawSqlValidationResult.Fail("Multiple SQL statements are not allowed. Only a single SELECT is permitted.");

            // Check for forbidden keywords first for specific error messages
            var match = ForbiddenKeywords.Match(stripped);
            if (match.Success)
                return RawSqlValidationResult.Fail($"Forbidden SQL keyword detected: {match.Value.ToUpperInvariant()}");

            // Check that it starts with SELECT (after stripping whitespace)
            var trimmed = stripped.Trim();
            if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                return RawSqlValidationResult.Fail("Only SELECT queries (optionally with WITH/CTE) are allowed.");

            return RawSqlValidationResult.Ok();
        }
    }

    public readonly struct RawSqlValidationResult
    {
        public bool IsValid { get; }
        public string? ErrorMessage { get; }

        private RawSqlValidationResult(bool isValid, string? errorMessage)
        {
            IsValid = isValid;
            ErrorMessage = errorMessage;
        }

        public static RawSqlValidationResult Ok() => new(true, null);
        public static RawSqlValidationResult Fail(string errorMessage) => new(false, errorMessage);
    }
}
