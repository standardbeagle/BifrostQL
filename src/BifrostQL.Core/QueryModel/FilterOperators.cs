namespace BifrostQL.Core.QueryModel
{
    /// <summary>
    /// The single canonical set of BifrostQL filter-operator tokens (the GraphQL
    /// <c>_eq</c>, <c>_in</c>, <c>_contains</c>, … names). Both SQL builders consume
    /// these constants instead of scattering raw string literals: the query-filter
    /// path (<see cref="TableFilter.GetSingleFilterParameterized"/>) and the visual
    /// query path (<c>VisualQueryBuilder</c>). The negated / pattern variants that
    /// only the query-filter path uses live here too so there is one home for the
    /// whole vocabulary.
    /// </summary>
    public static class FilterOperators
    {
        public const string Eq = "_eq";
        public const string Neq = "_neq";
        public const string Lt = "_lt";
        public const string Lte = "_lte";
        public const string Gt = "_gt";
        public const string Gte = "_gte";

        public const string Contains = "_contains";
        public const string NContains = "_ncontains";
        public const string StartsWith = "_starts_with";
        public const string NStartsWith = "_nstarts_with";
        public const string EndsWith = "_ends_with";
        public const string NEndsWith = "_nends_with";

        public const string Like = "_like";
        public const string NLike = "_nlike";

        public const string In = "_in";
        public const string NIn = "_nin";

        public const string Between = "_between";
        public const string NBetween = "_nbetween";

        public const string Null = "_null";

        /// <summary>
        /// Cross-dialect full-text search operator. Unlike every operator above — each of
        /// which is COLUMN-scoped (it appears on a column's <c>FilterType…Input</c>) —
        /// <c>_search</c> is TABLE-scoped: it matches a single query string against the set
        /// of columns a table declares searchable via the <c>search</c> table metadata
        /// (see <see cref="BifrostQL.Core.Modules.Fts.FtsConfig"/>). It is therefore surfaced
        /// on the table's filter input, not on any per-column filter type, and only for
        /// tables that declare searchable columns.
        ///
        /// SEMANTIC CONTRACT (pinned here; the per-dialect lowering slice must implement this
        /// ONE behavior across SqlServer / Postgres / MySQL / SQLite whose native full-text
        /// defaults differ, rather than each engine's default):
        ///
        ///   • The query string is tokenized on unquoted whitespace into TERMS.
        ///   • A double-quoted run (<c>"foo bar"</c>) is a single PHRASE term matched as a
        ///     contiguous substring (spaces inside the quotes are literal, not separators).
        ///   • MULTI-TERM is AND-of-terms: a row matches only when EVERY term matches at
        ///     least one of the table's searchable columns (conjunctive — the intuitive
        ///     "narrow as I type" behavior, and the safe default because widening the
        ///     semantic later is non-breaking whereas narrowing it is). A single term
        ///     matches if it is a substring of any searchable column.
        ///   • Matching is case-insensitive.
        ///
        /// Example: <c>_search: "quick brown \"lazy dog\""</c> matches rows where SOME
        /// searchable column contains "quick", AND some contains "brown", AND some contains
        /// the exact phrase "lazy dog".
        /// </summary>
        public const string Search = "_search";
    }
}
