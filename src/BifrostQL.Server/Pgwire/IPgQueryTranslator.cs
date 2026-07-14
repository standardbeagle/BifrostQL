using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// A recognized simple query ready to run: the programmatic <see cref="QueryIntent"/>
    /// to execute through the read seam, and the ordered output columns to build the
    /// RowDescription and to project each result row into a DataRow.
    /// </summary>
    internal sealed class PgQueryPlan
    {
        public required QueryIntent Intent { get; init; }
        public required IReadOnlyList<PgResultColumn> Columns { get; init; }

        /// <summary>
        /// Number of <c>$N</c> extended-protocol placeholders the source statement carries
        /// (0 for a simple query). Drives the extended protocol's ParameterDescription.
        /// </summary>
        public int ParameterCount { get; init; }
    }

    /// <summary>
    /// A simple query string the translator could not turn into a runnable plan (not a
    /// recognized SELECT, an unknown table, an unknown column). It is a query-phase error,
    /// NOT a wire-protocol violation: the connection handler answers it with a non-fatal
    /// <c>ERROR</c> ErrorResponse and keeps the session alive, so it deliberately does NOT
    /// derive from <see cref="PgProtocolException"/> (that base tears the connection down).
    /// </summary>
    internal sealed class PgQueryTranslationException : Exception
    {
        public PgQueryTranslationException(string message) : base(message) { }

        public PgQueryTranslationException(string message, string sqlState) : base(message)
        {
            SqlState = sqlState;
        }

        /// <summary>
        /// The pg SQLSTATE to answer with. Defaults to <c>syntax_error</c> (an
        /// unrecognized statement); recognized-but-out-of-subset constructs
        /// (GROUP BY, UNION, functions, subqueries, non-INNER joins) set
        /// <c>feature_not_supported</c> so the client can tell the two apart.
        /// </summary>
        public string SqlState { get; } = PgWireProtocol.SqlStateSyntaxError;
    }

    /// <summary>
    /// Turns a PostgreSQL simple query string into a runnable <see cref="PgQueryPlan"/>
    /// against a Bifrost endpoint. Kept behind an interface so the protocol/encoding code
    /// in <see cref="PgConnectionHandler"/> depends only on this seam.
    /// </summary>
    internal interface IPgQueryTranslator
    {
        Task<PgQueryPlan> TranslateAsync(
            IQueryIntentExecutor executor,
            string sql,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken);

        /// <summary>
        /// Extended-protocol translate: like the simple-query overload, but each <c>$N</c>
        /// placeholder in the SQL is resolved to <paramref name="parameters"/><c>[N-1]</c>
        /// as a BOUND filter value (never concatenated). Pass <c>null</c> to translate for
        /// column/parameter description only (Describe before Bind): placeholders resolve to
        /// null, the intent's filter is a throwaway, and only <see cref="PgQueryPlan.Columns"/>
        /// and <see cref="PgQueryPlan.ParameterCount"/> are meaningful. A placeholder ordinal
        /// past the supplied parameter count is a bind mismatch and is rejected.
        /// </summary>
        Task<PgQueryPlan> TranslateAsync(
            IQueryIntentExecutor executor,
            string sql,
            IReadOnlyList<object?>? parameters,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken);
    }
}
