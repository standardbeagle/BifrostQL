using System.Text;
using System.Text.RegularExpressions;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// The quoting and comment rules a database engine's SQL scanner uses. The
    /// <see cref="RawSqlValidator"/> must split text into code, strings and comments
    /// exactly as the engine that runs it does, so each dialect names its own.
    /// </summary>
    public enum RawSqlLexicon
    {
        /// <summary>T-SQL: <c>[..]</c> identifiers, nested block comments, <c>--</c> always a comment.</summary>
        SqlServer,
        /// <summary>PostgreSQL: <c>$tag$..$tag$</c> strings, <c>E'..'</c> backslash strings, nested block comments.</summary>
        Postgres,
        /// <summary>MySQL / MariaDB: backslash escapes in <c>'..'</c> and <c>"..."</c>, <c>#</c> comments, <c>-- </c> needs whitespace.</summary>
        MySql,
        /// <summary>SQLite: <c>[..]</c> and <c>`..`</c> identifiers without escapes, non-nesting block comments.</summary>
        Sqlite,
    }

    /// <summary>
    /// Lexical gate in front of <c>_rawQuery</c>: the text must be a single SELECT
    /// (optionally with a CTE) and name none of the verbs or functions that write,
    /// change the session or reach outside the database.
    ///
    /// It is the secondary guard. A lexical check cannot see a write made inside a
    /// function the SELECT calls; <see cref="RawSqlExecutor.ExecuteReadOnlyAsync"/>
    /// stops that by running the text inside the dialect's
    /// <see cref="QueryModel.ISqlDialect.BeginReadOnlyScopeAsync"/>.
    ///
    /// The validator lexes the exact text that will be executed — it never
    /// validates a rewritten copy. String literals, quoted identifiers and
    /// comments are found by a character scanner that follows the target
    /// engine's rules (<see cref="RawSqlLexicon"/>) and are excluded from the
    /// keyword and statement-boundary checks; everything else is code. Where an
    /// engine's behaviour depends on a server setting, every setting is lexed
    /// and the text must pass under each:
    /// - MySQL with and without backslash escapes (<c>NO_BACKSLASH_ESCAPES</c>,
    ///   <c>ANSI_QUOTES</c>).
    /// - PostgreSQL with <c>standard_conforming_strings</c> on and off.
    /// Line comments end at the first CR or LF on every lexicon, the earliest
    /// end any engine uses. Executable comments (<c>/*!..*/</c>, <c>/*M!..*/</c>)
    /// are refused outright.
    /// </summary>
    public sealed class RawSqlValidator
    {
        // Verbs that write, change the session, end the read-only transaction or reach
        // outside the database, plus functions with the same effect. Matched at word
        // boundaries in the code view, case-insensitive.
        private static readonly Regex ForbiddenKeywords = new(
            @"\b(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|RENAME|EXEC|EXECUTE|CALL|DO|GRANT|REVOKE|DENY"
            + @"|BACKUP|RESTORE|SHUTDOWN|DBCC|BULK|OPENROWSET|OPENQUERY|OPENDATASOURCE|OPENXML|INTO"
            + @"|WRITETEXT|UPDATETEXT|DISABLE|ENABLE|KILL|USE|SET|DECLARE|WAITFOR|SETUSER|RECONFIGURE"
            + @"|COMMIT|ROLLBACK|SAVEPOINT|LOCK|UNLOCK|HANDLER|PREPARE|DEALLOCATE|COPY|VACUUM|LOAD"
            + @"|PRAGMA|ATTACH|DETACH"
            + @"|SET_CONFIG|SETVAL|NEXTVAL|PG_TERMINATE_BACKEND|PG_CANCEL_BACKEND|PG_RELOAD_CONF|PG_ROTATE_LOGFILE"
            + @"|DBLINK\w*|LO_\w+|PG_READ_FILE|PG_READ_BINARY_FILE|PG_LS_DIR|PG_FILE_WRITE"
            + @"|LOAD_FILE|LOAD_EXTENSION)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SelectOrWithPrefix = new(
            @"^\s*(SELECT|WITH)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly LexRules[] _interpretations;

        public RawSqlValidator(RawSqlLexicon lexicon)
        {
            _interpretations = InterpretationsFor(lexicon);
        }

        /// <summary>
        /// Validates that the SQL is a single SELECT statement with no dangerous operations.
        /// </summary>
        /// <returns>A validation result with success/failure and error message.</returns>
        public RawSqlValidationResult Validate(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return RawSqlValidationResult.Fail("SQL query must not be empty.");

            foreach (var rules in _interpretations)
            {
                var result = ValidateInterpretation(sql, rules);
                if (!result.IsValid)
                    return result;
            }
            return RawSqlValidationResult.Ok();
        }

        private static RawSqlValidationResult ValidateInterpretation(string sql, LexRules rules)
        {
            if (!TryExtractCode(sql, rules, out var code, out var error))
                return RawSqlValidationResult.Fail(error);

            // Statement boundary: a semicolon followed by anything other than
            // whitespace means a second statement. A single trailing semicolon
            // is permitted.
            var semicolon = code.IndexOf(';');
            if (semicolon >= 0 && code.AsSpan(semicolon + 1).Trim().Length > 0)
                return RawSqlValidationResult.Fail("Multiple SQL statements are not allowed. Only a single SELECT is permitted.");

            var match = ForbiddenKeywords.Match(code);
            if (match.Success)
                return RawSqlValidationResult.Fail($"Forbidden SQL keyword detected: {match.Value.ToUpperInvariant()}");

            if (!SelectOrWithPrefix.IsMatch(code))
                return RawSqlValidationResult.Fail("Only SELECT queries (optionally with WITH/CTE) are allowed.");

            return RawSqlValidationResult.Ok();
        }

        /// <summary>One engine scanner's rules for strings, identifiers and comments.</summary>
        private sealed record LexRules(
            bool NestedBlockComments,
            bool DashDashNeedsWhitespace,
            bool HashComments,
            bool BracketIdentifiers,
            bool BracketDoubling,
            bool BacktickIdentifiers,
            bool DollarQuotes,
            bool EscapeStringPrefix,
            bool BackslashEscapes);

        private static LexRules[] InterpretationsFor(RawSqlLexicon lexicon) => lexicon switch
        {
            RawSqlLexicon.SqlServer => new[]
            {
                new LexRules(NestedBlockComments: true, DashDashNeedsWhitespace: false, HashComments: false,
                    BracketIdentifiers: true, BracketDoubling: true, BacktickIdentifiers: false,
                    DollarQuotes: false, EscapeStringPrefix: false, BackslashEscapes: false),
            },
            RawSqlLexicon.Postgres => new[]
            {
                // standard_conforming_strings = on (default) and off.
                new LexRules(NestedBlockComments: true, DashDashNeedsWhitespace: false, HashComments: false,
                    BracketIdentifiers: false, BracketDoubling: false, BacktickIdentifiers: false,
                    DollarQuotes: true, EscapeStringPrefix: true, BackslashEscapes: false),
                new LexRules(NestedBlockComments: true, DashDashNeedsWhitespace: false, HashComments: false,
                    BracketIdentifiers: false, BracketDoubling: false, BacktickIdentifiers: false,
                    DollarQuotes: true, EscapeStringPrefix: true, BackslashEscapes: true),
            },
            RawSqlLexicon.MySql => new[]
            {
                // Default sql_mode, then NO_BACKSLASH_ESCAPES / ANSI_QUOTES.
                new LexRules(NestedBlockComments: false, DashDashNeedsWhitespace: true, HashComments: true,
                    BracketIdentifiers: false, BracketDoubling: false, BacktickIdentifiers: true,
                    DollarQuotes: false, EscapeStringPrefix: false, BackslashEscapes: true),
                new LexRules(NestedBlockComments: false, DashDashNeedsWhitespace: true, HashComments: true,
                    BracketIdentifiers: false, BracketDoubling: false, BacktickIdentifiers: true,
                    DollarQuotes: false, EscapeStringPrefix: false, BackslashEscapes: false),
            },
            RawSqlLexicon.Sqlite => new[]
            {
                new LexRules(NestedBlockComments: false, DashDashNeedsWhitespace: false, HashComments: false,
                    BracketIdentifiers: true, BracketDoubling: false, BacktickIdentifiers: true,
                    DollarQuotes: false, EscapeStringPrefix: false, BackslashEscapes: false),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(lexicon), lexicon, null),
        };

        /// <summary>
        /// Scans <paramref name="sql"/> under <paramref name="rules"/> and produces the
        /// code-only view: string literals, quoted identifiers, and comments are each
        /// replaced by a single space. Fails on unterminated constructs and on
        /// executable comments.
        /// </summary>
        private static bool TryExtractCode(string sql, LexRules rules, out string code, out string error)
        {
            var sb = new StringBuilder(sql.Length);
            var i = 0;
            code = string.Empty;
            while (i < sql.Length)
            {
                var c = sql[i];
                if (c == '\'')
                {
                    var backslash = rules.BackslashEscapes
                        || (rules.EscapeStringPrefix && IsEscapeStringPrefix(sql, i));
                    if (!TrySkipQuoted(sql, ref i, '\'', doubling: true, backslash))
                    {
                        error = "Unterminated string literal.";
                        return false;
                    }
                }
                else if (c == '"')
                {
                    if (!TrySkipQuoted(sql, ref i, '"', doubling: true, rules.BackslashEscapes))
                    {
                        error = "Unterminated quoted identifier.";
                        return false;
                    }
                }
                else if (c == '`' && rules.BacktickIdentifiers)
                {
                    if (!TrySkipQuoted(sql, ref i, '`', doubling: true, backslashEscapes: false))
                    {
                        error = "Unterminated quoted identifier.";
                        return false;
                    }
                }
                else if (c == '[' && rules.BracketIdentifiers)
                {
                    if (!TrySkipQuoted(sql, ref i, ']', rules.BracketDoubling, backslashEscapes: false))
                    {
                        error = "Unterminated quoted identifier.";
                        return false;
                    }
                }
                else if (c == '$' && rules.DollarQuotes && TryReadDollarTag(sql, i, out var tag))
                {
                    var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        error = "Unterminated string literal.";
                        return false;
                    }
                    i = close + tag.Length;
                }
                else if (c == '/' && At(sql, i + 1, '*'))
                {
                    if (At(sql, i + 2, '!') || (At(sql, i + 2, 'M') && At(sql, i + 3, '!')))
                    {
                        error = "Executable comment syntax (/*!...*/) is not allowed.";
                        return false;
                    }
                    if (!TrySkipBlockComment(sql, ref i, rules.NestedBlockComments))
                    {
                        error = "Unterminated block comment.";
                        return false;
                    }
                }
                else if (c == '-' && At(sql, i + 1, '-')
                         && (!rules.DashDashNeedsWhitespace || i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2])))
                {
                    i = SkipLineComment(sql, i + 2);
                }
                else if (c == '#' && rules.HashComments)
                {
                    i = SkipLineComment(sql, i + 1);
                }
                else
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                sb.Append(' ');
            }

            code = sb.ToString();
            error = string.Empty;
            return true;
        }

        private static bool At(string sql, int index, char expected)
            => index < sql.Length && sql[index] == expected;

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

        // PostgreSQL E'...' / e'...': the prefix letter must start its own token.
        private static bool IsEscapeStringPrefix(string sql, int quote)
            => quote >= 1 && (sql[quote - 1] == 'E' || sql[quote - 1] == 'e')
               && (quote < 2 || !IsIdentifierChar(sql[quote - 2]));

        // PostgreSQL dollar quote opener: $$ or $tag$ with tag = [A-Za-z_][A-Za-z0-9_]*,
        // not inside an identifier (where $ is an ordinary character) and not $1.
        private static bool TryReadDollarTag(string sql, int start, out string tag)
        {
            tag = string.Empty;
            if (start > 0 && IsIdentifierChar(sql[start - 1]))
                return false;
            var j = start + 1;
            if (j < sql.Length && (char.IsLetter(sql[j]) || sql[j] == '_'))
            {
                j++;
                while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_'))
                    j++;
            }
            if (j >= sql.Length || sql[j] != '$')
                return false;
            tag = sql.Substring(start, j - start + 1);
            return true;
        }

        private static int SkipLineComment(string sql, int from)
        {
            var end = sql.IndexOfAny(LineEnds, from);
            return end < 0 ? sql.Length : end + 1;
        }

        private static readonly char[] LineEnds = { '\r', '\n' };

        private static bool TrySkipBlockComment(string sql, ref int i, bool nested)
        {
            var depth = 1;
            var j = i + 2;
            while (j < sql.Length)
            {
                if (sql[j] == '*' && At(sql, j + 1, '/'))
                {
                    j += 2;
                    if (--depth == 0)
                    {
                        i = j;
                        return true;
                    }
                    continue;
                }
                if (nested && sql[j] == '/' && At(sql, j + 1, '*'))
                {
                    depth++;
                    j += 2;
                    continue;
                }
                j++;
            }
            return false;
        }

        /// <summary>
        /// Advances <paramref name="i"/> past a quoted region. On entry
        /// <paramref name="i"/> points at the opening delimiter; on success it
        /// points just past the closing <paramref name="closeQuote"/>.
        /// </summary>
        private static bool TrySkipQuoted(string sql, ref int i, char closeQuote, bool doubling, bool backslashEscapes)
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
                    if (doubling && i + 1 < sql.Length && sql[i + 1] == closeQuote)
                    {
                        i += 2;
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
