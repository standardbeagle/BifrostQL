using System.Collections.Generic;
using System.Text;

namespace BifrostQL.Core.QueryModel
{
    /// <summary>
    /// One parsed term of a <c>_search</c> query. A bare whitespace-delimited word is a
    /// non-phrase term; a double-quoted run is a phrase term whose words must appear
    /// contiguously. See <see cref="FilterOperators.Search"/> for the pinned semantic
    /// (space-separated terms are AND-ed; a double-quoted run is a contiguous phrase;
    /// matching is case-insensitive).
    /// </summary>
    /// <param name="Text">The term text with any surrounding quotes stripped (never wire-escaped).</param>
    /// <param name="IsPhrase">Whether the term was double-quoted (contiguous-phrase match).</param>
    public readonly record struct FtsTerm(string Text, bool IsPhrase);

    /// <summary>
    /// Everything a dialect needs to lower a <c>_search</c> operator into its engine's
    /// full-text predicate, WITHOUT ever interpolating user text into SQL. The
    /// <see cref="Terms"/> are the parsed query terms; a dialect binds each term's value
    /// as a parameter through <see cref="Parameters"/> (never into the SQL text — the
    /// engines' full-text query grammars, e.g. tsquery and FTS5 MATCH, are themselves
    /// injectable languages) and references only the schema-derived, validated
    /// <see cref="ColumnNames"/> in the emitted predicate. Raw (unescaped) identifiers are
    /// passed so the dialect applies its own <c>EscapeIdentifier</c>.
    /// </summary>
    /// <param name="TableAlias">The alias the query gives the base table in its FROM, or null.</param>
    /// <param name="TableSchema">The base table's schema (may be empty).</param>
    /// <param name="TableName">The base table's raw name (unescaped).</param>
    /// <param name="ColumnNames">The validated searchable columns (raw, from <c>FtsConfig</c>).</param>
    /// <param name="KeyColumnNames">The base table's primary-key columns (raw) — used by engines (SQLite FTS5) that correlate an external-content index by rowid/key.</param>
    /// <param name="Terms">The parsed query terms (already tokenized; never empty when called).</param>
    /// <param name="Language">The optional <c>search-language</c> hint, or null.</param>
    /// <param name="Parameters">The parameter collection the dialect binds term values into.</param>
    public sealed record FtsPredicateRequest(
        string? TableAlias,
        string TableSchema,
        string TableName,
        IReadOnlyList<string> ColumnNames,
        IReadOnlyList<string> KeyColumnNames,
        IReadOnlyList<FtsTerm> Terms,
        string? Language,
        SqlParameterCollection Parameters);

    /// <summary>
    /// Tokenizes a <c>_search</c> query string into <see cref="FtsTerm"/>s per the pinned
    /// semantic: whitespace separates terms, a double-quoted run is a single contiguous
    /// phrase (its inner whitespace is literal), and empty/whitespace input yields no
    /// terms (an empty search adds no predicate rather than matching everything or
    /// nothing by accident). The parser never emits SQL — it only splits the raw string;
    /// each resulting term's VALUE is bound as a parameter by the dialect.
    /// </summary>
    public static class FtsQueryParser
    {
        public static IReadOnlyList<FtsTerm> Parse(string? query)
        {
            var terms = new List<FtsTerm>();
            if (string.IsNullOrWhiteSpace(query))
                return terms;

            var current = new StringBuilder();
            var inQuotes = false;

            void Flush(bool phrase)
            {
                if (current.Length == 0)
                    return;
                terms.Add(new FtsTerm(current.ToString(), phrase));
                current.Clear();
            }

            foreach (var ch in query)
            {
                if (ch == '"')
                {
                    // Close of a quoted run is a phrase term even when empty-inside is
                    // skipped by Flush; open of a run flushes any pending bare word first.
                    if (inQuotes)
                        Flush(phrase: true);
                    else
                        Flush(phrase: false);
                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && char.IsWhiteSpace(ch))
                {
                    Flush(phrase: false);
                    continue;
                }

                current.Append(ch);
            }

            // A trailing unbalanced quote is treated as having closed the phrase at
            // end-of-string rather than dropping it (fail-safe: still a bound value).
            Flush(phrase: inQuotes);
            return terms;
        }
    }
}
