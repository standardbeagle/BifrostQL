using System.Text;
using System.Text.RegularExpressions;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Validates that raw SQL queries contain only a single SELECT statement.
    /// Rejects any data modification (INSERT, UPDATE, DELETE, MERGE),
    /// schema modification (CREATE, ALTER, DROP, TRUNCATE), or
    /// execution statements (EXEC, EXECUTE, xp_, sp_).
    ///
    /// The validator lexes the exact text that will be executed — it never
    /// validates a rewritten copy of the SQL. Comment and string-literal regions
    /// are identified by a character-level scanner and excluded from the keyword
    /// and statement-boundary checks; everything the scanner classifies as code
    /// is checked as code.
    ///
    /// Because BifrostQL targets multiple dialects (SQL Server, PostgreSQL,
    /// MySQL, SQLite) whose escape rules differ, the scanner is deliberately
    /// conservative:
    /// - String literals are lexed twice — once with backslash-escape semantics
    ///   (MySQL default) and once with quote-doubling-only semantics (ANSI /
    ///   SQL Server / PostgreSQL / SQLite). The SQL must be a valid single
    ///   SELECT under BOTH interpretations, so a payload that reads as a
    ///   harmless string in one dialect but executable code in another is
    ///   rejected.
    /// - MySQL executable comments (<c>/*! ... */</c>) are rejected outright:
    ///   MySQL executes their contents even though other engines treat them as
    ///   comments.
    /// - <c>--</c> starts a comment only when followed by whitespace or
    ///   end-of-input (MySQL's rule). A bare <c>--x</c> sequence is scanned as
    ///   code, because MySQL executes it.
    /// - <c>#</c> is never treated as a comment starter; MySQL comment text
    ///   after <c>#</c> is scanned as code (over-strict, never under-strict).
    /// </summary>
    public sealed class RawSqlValidator
    {
        // Matches dangerous keywords at word boundaries, case-insensitive.
        // These must not appear in the code (non-comment, non-string) portion of the SQL.
        private static readonly Regex ForbiddenKeywords = new(
            @"\b(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC|EXECUTE|GRANT|REVOKE|DENY|BACKUP|RESTORE|SHUTDOWN|DBCC|BULK|OPENROWSET|OPENQUERY|OPENDATASOURCE|INTO)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SelectOrWithPrefix = new(
            @"^\s*(SELECT|WITH)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Validates that the SQL is a single SELECT statement with no dangerous operations.
        /// </summary>
        /// <returns>A validation result with success/failure and error message.</returns>
        public RawSqlValidationResult Validate(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return RawSqlValidationResult.Fail("SQL query must not be empty.");

            // The SQL must validate under both string-escape interpretations —
            // see class remarks. Report the first failure.
            var result = ValidateInterpretation(sql, backslashEscapes: false);
            if (!result.IsValid)
                return result;
            return ValidateInterpretation(sql, backslashEscapes: true);
        }

        private static RawSqlValidationResult ValidateInterpretation(string sql, bool backslashEscapes)
        {
            if (!TryExtractCode(sql, backslashEscapes, out var code, out var error))
                return RawSqlValidationResult.Fail(error);

            // Statement boundary: a semicolon followed by anything other than
            // whitespace means a second statement. A single trailing semicolon
            // is permitted.
            var semicolon = code.IndexOf(';');
            if (semicolon >= 0 && code.AsSpan(semicolon + 1).Trim().Length > 0)
                return RawSqlValidationResult.Fail("Multiple SQL statements are not allowed. Only a single SELECT is permitted.");

            // Check for forbidden keywords first for specific error messages
            var match = ForbiddenKeywords.Match(code);
            if (match.Success)
                return RawSqlValidationResult.Fail($"Forbidden SQL keyword detected: {match.Value.ToUpperInvariant()}");

            if (!SelectOrWithPrefix.IsMatch(code))
                return RawSqlValidationResult.Fail("Only SELECT queries (optionally with WITH/CTE) are allowed.");

            return RawSqlValidationResult.Ok();
        }

        /// <summary>
        /// Scans <paramref name="sql"/> and produces the code-only view: string
        /// literals, quoted identifiers, and comments are each replaced by a
        /// single space. Fails on unterminated constructs and on MySQL
        /// executable comments.
        /// </summary>
        private static bool TryExtractCode(string sql, bool backslashEscapes, out string code, out string error)
        {
            var sb = new StringBuilder(sql.Length);
            var i = 0;
            while (i < sql.Length)
            {
                var c = sql[i];
                switch (c)
                {
                    case '\'':
                        if (!TrySkipQuoted(sql, ref i, '\'', backslashEscapes))
                        {
                            code = string.Empty;
                            error = "Unterminated string literal.";
                            return false;
                        }
                        sb.Append(' ');
                        break;
                    case '"':
                    case '`':
                        if (!TrySkipQuoted(sql, ref i, c, backslashEscapes: false))
                        {
                            code = string.Empty;
                            error = "Unterminated quoted identifier.";
                            return false;
                        }
                        sb.Append(' ');
                        break;
                    case '[':
                        if (!TrySkipQuoted(sql, ref i, ']', backslashEscapes: false))
                        {
                            code = string.Empty;
                            error = "Unterminated quoted identifier.";
                            return false;
                        }
                        sb.Append(' ');
                        break;
                    case '/' when i + 1 < sql.Length && sql[i + 1] == '*':
                        if (i + 2 < sql.Length && sql[i + 2] == '!')
                        {
                            code = string.Empty;
                            error = "Executable comment syntax (/*!...*/) is not allowed.";
                            return false;
                        }
                        var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        if (end < 0)
                        {
                            code = string.Empty;
                            error = "Unterminated block comment.";
                            return false;
                        }
                        // Non-nesting scan: every engine's comment region is a
                        // superset of [i, end + 2), so nothing treated as a
                        // comment here can execute as code on any dialect.
                        i = end + 2;
                        sb.Append(' ');
                        break;
                    case '-' when i + 1 < sql.Length && sql[i + 1] == '-'
                                  && (i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2])):
                        // Line comment on all supported dialects (MySQL requires
                        // the trailing whitespace, hence the guard above).
                        var newline = sql.IndexOf('\n', i + 2);
                        i = newline < 0 ? sql.Length : newline + 1;
                        sb.Append(' ');
                        break;
                    default:
                        sb.Append(c);
                        i++;
                        break;
                }
            }

            code = sb.ToString();
            error = string.Empty;
            return true;
        }

        /// <summary>
        /// Advances <paramref name="i"/> past a quoted region. On entry
        /// <paramref name="i"/> points at the opening delimiter; on success it
        /// points just past the closing <paramref name="closeQuote"/>. Doubled
        /// close-quotes always escape; backslash escapes apply only when
        /// <paramref name="backslashEscapes"/> is set.
        /// </summary>
        private static bool TrySkipQuoted(string sql, ref int i, char closeQuote, bool backslashEscapes)
        {
            i++; // skip opening delimiter
            while (i < sql.Length)
            {
                var c = sql[i];
                if (backslashEscapes && c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == closeQuote)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == closeQuote)
                    {
                        i += 2; // doubled quote — escaped
                        continue;
                    }
                    i++;
                    return true;
                }
                i++;
            }
            return false;
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
