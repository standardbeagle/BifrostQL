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
    }
}
