using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel
{
    /// <summary>
    /// The window a bounded read asks for when it must tell a complete answer from one the
    /// row ceiling cut short: <see cref="Fetch"/> is the LIMIT the query carries (one past
    /// the page, as the truncation sentinel) and <see cref="Page"/> is the most rows the
    /// surface may hand back.
    ///
    /// <para>Both come from <see cref="GqlObjectQuery.ResolveRowWindow"/>, the same clamp
    /// SQL generation applies. A caller that sets <c>Limit = page + 1</c> itself and
    /// compares against its own page never sees that clamp: under <c>max-query-rows</c>
    /// below the page, the database returns the ceiling, the count never exceeds the page,
    /// and the read reports a short answer as complete. Every adapter that pages or caps a
    /// read takes its numbers from here instead.</para>
    /// </summary>
    public readonly record struct RowProbeWindow
    {
        private RowProbeWindow(int page, int fetch, bool clamped)
        {
            Page = page;
            Fetch = fetch;
            Clamped = clamped;
        }

        /// <summary>The most rows the surface returns: the requested page, narrowed by the ceiling.</summary>
        public int Page { get; }

        /// <summary>The LIMIT the query carries: <see cref="Page"/> + 1.</summary>
        public int Fetch { get; }

        /// <summary>
        /// True when the ceiling, not the caller, set <see cref="Page"/>: the request was
        /// unbounded or asked for more than <c>max-query-rows</c> allows.
        /// </summary>
        public bool Clamped { get; }

        /// <summary>True when the read returned more than <see cref="Page"/> rows, so more rows exist.</summary>
        public bool HasMore(int rowCount) => rowCount > Page;

        /// <summary>
        /// True when rows beyond the page exist AND the ceiling chose the page: the caller
        /// asked for more than it got. A surface with no continuation reports this as an
        /// explicit refusal or flag, never as a shorter result.
        /// </summary>
        public bool IsCutByCeiling(int rowCount) => Clamped && HasMore(rowCount);

        /// <summary>
        /// The probe window for <paramref name="requestedPage"/> rows. A negative request
        /// means "every row" and resolves to the ceiling. The page is at most one below
        /// the ceiling, because the sentinel row has to fit under it too.
        /// </summary>
        public static RowProbeWindow For(IDbModel model, int requestedPage)
        {
            ArgumentNullException.ThrowIfNull(model);
            var unbounded = requestedPage < 0 || requestedPage == int.MaxValue;
            var fetch = GqlObjectQuery.ResolveRowWindow(model, unbounded ? -1 : requestedPage + 1);
            var page = fetch - 1;
            return new RowProbeWindow(page, fetch, clamped: unbounded || requestedPage > page);
        }
    }
}
