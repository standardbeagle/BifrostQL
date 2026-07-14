namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// Configuration for the Redis RESP-protocol front door (slice 1: codec, connection
    /// loop, PING/HELLO/AUTH/SELECT/INFO plumbing, fail-closed identity). The data command
    /// surface it fronts (GET/SET/HGETALL/SCAN…) attaches at the dispatch seam in later
    /// slices; the query surface those commands read is selected by <see cref="Endpoint"/>.
    /// </summary>
    public sealed class RespWireOptions
    {
        /// <summary>TCP port the front door listens on. Default 6379 (the Redis port).</summary>
        public int Port { get; set; } = 6379;

        /// <summary>
        /// When <c>true</c> (the default), a connection must complete <c>AUTH</c> (or inline
        /// <c>HELLO … AUTH</c>) before any command that needs an identity runs; until then
        /// those commands are refused with <c>NOAUTH</c>. There is no anonymous mode unless a
        /// deployment explicitly sets this <c>false</c> — the front door never establishes a
        /// session with a subject-less/anonymous identity while authentication is required.
        /// </summary>
        public bool RequireAuthentication { get; set; } = true;

        /// <summary>
        /// Hard cap on the byte length of any single bulk/verbatim string and on the length
        /// of any inline line, applied on the UNAUTHENTICATED path (DoS guard). A hostile
        /// length prefix beyond this is refused with a protocol error, never allocated —
        /// mirrors the pgwire <c>MaxMessageLength</c> guard. Default 1 MiB; per-command
        /// larger limits for data writes arrive with the data slices.
        /// </summary>
        public int MaxBulkLength { get; set; } = 1 << 20;

        /// <summary>
        /// Hard cap on the declared element count of any array/set/push/map, applied on the
        /// UNAUTHENTICATED path (DoS guard) so a huge multibulk count cannot pre-allocate an
        /// unbounded array. Default 1,048,576.
        /// </summary>
        public int MaxAggregateElements { get; set; } = 1 << 20;

        /// <summary>
        /// Registered BifrostQL endpoint path (e.g. <c>/graphql</c>) whose model, schema and
        /// connection authenticated sessions execute their data commands against. Null selects
        /// the single registered endpoint. Unused by slice-1 plumbing; carried for the data
        /// slices.
        /// </summary>
        public string? Endpoint { get; set; }
    }
}
