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
        /// Hard cap on how deeply aggregates (array/set/push/map) may nest, applied on the
        /// UNAUTHENTICATED path (DoS guard). The recursive decoder consumes one physical stack
        /// frame per nesting level, and because buffered socket bytes let the reads complete
        /// synchronously, an unauthenticated peer sending a few KB of repeated aggregate headers
        /// (e.g. <c>*1\r\n</c>×N) would otherwise grow the stack without bound until an
        /// uncatchable <c>StackOverflowException</c> tears down the whole host process. The
        /// decoder refuses to descend past this cap, raising a clean protocol error the
        /// connection loop handles instead. Default 32: real RESP3 traffic (push → array of
        /// maps, HELLO reply maps, nested command arrays) nests only a handful deep, so 32 is
        /// generous headroom while a chain that deep is unambiguously hostile.
        /// </summary>
        public int MaxNestingDepth { get; set; } = 32;

        /// <summary>
        /// Registered BifrostQL endpoint path (e.g. <c>/graphql</c>) whose model, schema and
        /// connection authenticated sessions execute their data commands against. Null selects
        /// the single registered endpoint. Unused by slice-1 plumbing; carried for the data
        /// slices.
        /// </summary>
        public string? Endpoint { get; set; }

        /// <summary>
        /// Master gate for the RESP WRITE surface (SET/HSET/DEL). <b>Off by default</b>: until a
        /// deployment explicitly opts in by setting this <c>true</c>, every write command is
        /// refused with a clean <c>-ERR</c> and executes NOTHING — the front door exposes reads
        /// only. This is a fail-closed posture: a write path is the highest-risk surface, so it
        /// stays dark unless deliberately turned on. When enabled, writes route through
        /// <c>IMutationIntentExecutor</c> under the session identity, so the full mutation
        /// transformer chain (tenant scoping, audit actor, soft-delete, field-encryption-on-write,
        /// CDC/history hooks) applies and is unskippable. Enabling it is logged at startup as a
        /// notable posture change.
        /// </summary>
        public bool EnableWrites { get; set; }
    }
}
